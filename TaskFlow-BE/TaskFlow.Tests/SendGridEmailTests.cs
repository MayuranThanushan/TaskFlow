using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using TaskFlow.Application.Features.Email;
using TaskFlow.Infrastructure.Email;

namespace TaskFlow.Tests;

public sealed class SendGridEmailTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.Accepted;
        public string? Payload { get; private set; }
        public HttpRequestMessage? Message { get; private set; }
        public Exception? Failure { get; set; }
        public RetryConditionHeaderValue? RetryAfter { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Message = request; Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            var response = new HttpResponseMessage(Status) { Content = new UnreadableContent() };
            response.Headers.RetryAfter = RetryAfter; return response;
        }
    }
    private sealed class UnreadableContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => throw new InvalidOperationException("Provider response bodies must never be read or logged");
    }
    private static LocalEmailOptions Options() => new()
    {
        Mode = "SendGrid", PublicOrigin = "https://pilot.example.test", AllowedRecipients = ["recipient@example.test"],
        SendGrid = new() { ApiKey = "synthetic-sendgrid-key-for-tests", FromEmail = "sender@example.test", FromName = "TaskFlow" }
    };
    private static EmailRequest Request(EmailTemplate template = EmailTemplate.CompanyInvitation) => new()
    {
        WorkspaceId = template is EmailTemplate.EmailVerification or EmailTemplate.PasswordRecovery ? 0 : 7,
        AccountUserId = template is EmailTemplate.EmailVerification or EmailTemplate.PasswordRecovery ? 11 : null,
        InvitationId = template == EmailTemplate.CompanyInvitation ? 5 : null,
        MessageId = Guid.NewGuid().ToString("N"), Recipient = "recipient@example.test", Template = template,
        DisplayName = "<Alex>", CompanyName = "A & B", InvitationToken = new string('i', 43), SecurityToken = new string('s', 64), ProjectId = 9, TaskId = 12, TaskTitle = "Review release"
    };

    [Theory]
    [InlineData(EmailTemplate.CompanyInvitation)]
    [InlineData(EmailTemplate.EmailVerification)]
    [InlineData(EmailTemplate.PasswordRecovery)]
    public async Task ProviderAcceptanceUsesCorrectPrivateRequestAndDoesNotClaimDelivery(EmailTemplate template)
    {
        var handler = new Handler(); using var http = new HttpClient(handler); var options = Options(); var request = Request(template);
        var result = await new SendGridEmailSender(options, "Staging", http, new Clock()).SendAsync(request);
        Assert.Equal(EmailStatus.ProviderAccepted, result.Status); Assert.True(result.IsAccepted); Assert.False(result.IsCaptured);
        Assert.Equal(HttpMethod.Post, handler.Message!.Method); Assert.Equal("https://api.sendgrid.com/v3/mail/send", handler.Message.RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.Message.Headers.Authorization!.Scheme); Assert.Equal(options.SendGrid.ApiKey, handler.Message.Headers.Authorization.Parameter);
        using var payload = JsonDocument.Parse(handler.Payload!); var body = payload.RootElement;
        Assert.Equal(request.Recipient, body.GetProperty("personalizations")[0].GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal(options.SendGrid.FromEmail, body.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal(request.MessageId, body.GetProperty("custom_args").GetProperty("taskflow_message_id").GetString());
        Assert.False(body.GetProperty("tracking_settings").GetProperty("click_tracking").GetProperty("enable").GetBoolean());
        Assert.False(body.GetProperty("tracking_settings").GetProperty("open_tracking").GetProperty("enable").GetBoolean());
        Assert.Contains("https://pilot.example.test/", body.GetProperty("content")[0].GetProperty("value").GetString());
        Assert.DoesNotContain("No email has been sent", handler.Payload!); Assert.DoesNotContain(options.SendGrid.ApiKey, handler.Payload!);
        Assert.DoesNotContain(request.Recipient, result.ToString()); Assert.DoesNotContain(request.SecurityToken, result.ToString());
    }

    [Theory]
    [InlineData("Production", "SendGrid")]
    [InlineData("Development", "SendGrid")]
    [InlineData("Staging", "Disabled")]
    [InlineData("Staging", "LocalCapture")]
    public async Task DefaultAndUnapprovedEnvironmentsNeverContactProvider(string environment, string mode)
    {
        var options = Options(); options.Mode = mode; var handler = new Handler(); using var http = new HttpClient(handler);
        Assert.Equal(EmailStatus.Disabled, (await new SendGridEmailSender(options, environment, http, new Clock()).SendAsync(Request())).Status);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("key")]
    [InlineData("origin")]
    [InlineData("sender")]
    [InlineData("name")]
    [InlineData("allowlist")]
    public async Task InvalidConfigurationNeverContactsProvider(string invalid)
    {
        var options = Options();
        switch (invalid)
        {
            case "key": options.SendGrid.ApiKey = "private\r\ncanary"; break;
            case "origin": options.PublicOrigin = "http://pilot.example.test"; break;
            case "sender": options.SendGrid.FromEmail = "sender@example.test\r\nBcc:other@example.test"; break;
            case "name": options.SendGrid.FromName = "TaskFlow\r\nprivate"; break;
            case "allowlist": options.AllowedRecipients = []; break;
        }
        var handler = new Handler(); using var http = new HttpClient(handler);
        Assert.Equal("email.configuration_invalid", (await new SendGridEmailSender(options, "Staging", http, new Clock()).SendAsync(Request())).Code); Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task RecipientAllowlistIsExactAndMalformedRequestsNeverSend()
    {
        var options = Options(); options.AllowedRecipients = ["other@example.test"]; var handler = new Handler(); using var http = new HttpClient(handler);
        Assert.Equal(EmailStatus.RecipientBlocked, (await new SendGridEmailSender(options, "Staging", http, new Clock()).SendAsync(Request())).Status);
        Assert.Equal(EmailStatus.InvalidRequest, (await new SendGridEmailSender(options, "Staging", http, new Clock()).SendAsync(new())).Status); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(400, EmailStatus.ProviderRejected, "email.provider_rejected")]
    [InlineData(401, EmailStatus.ProviderRejected, "email.provider_auth_failed")]
    [InlineData(403, EmailStatus.ProviderRejected, "email.provider_auth_failed")]
    [InlineData(302, EmailStatus.ProviderRejected, "email.provider_rejected")]
    [InlineData(200, EmailStatus.ProviderRejected, "email.provider_rejected")]
    [InlineData(408, EmailStatus.ProviderUnavailable, "email.provider_unavailable")]
    [InlineData(500, EmailStatus.ProviderUnavailable, "email.provider_unavailable")]
    [InlineData(503, EmailStatus.ProviderUnavailable, "email.provider_unavailable")]
    public async Task FailureCodesAreClosedWithoutReadingResponseBodiesOrRetrying(int status, EmailStatus expected, string code)
    {
        var handler = new Handler { Status = (HttpStatusCode)status }; using var http = new HttpClient(handler);
        var result = await new SendGridEmailSender(Options(), "Staging", http, new Clock()).SendAsync(Request());
        Assert.Equal(expected, result.Status); Assert.Equal(code, result.Code); Assert.Equal(1, handler.Calls); Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task ProviderRetryAfterIsBoundedAndUsesTheInjectedClock()
    {
        var handler = new Handler { Status = (HttpStatusCode)429, RetryAfter = new(TimeSpan.FromDays(7)) }; using var http = new HttpClient(handler); var clock = new Clock();
        var sender = new SendGridEmailSender(Options(), "Staging", http, clock);
        Assert.Equal(clock.GetUtcNow().AddHours(1), (await sender.SendAsync(Request())).RetryAtUtc);
        handler.RetryAfter = new(clock.GetUtcNow().AddDays(-1));
        Assert.Equal(clock.GetUtcNow().AddSeconds(5), (await sender.SendAsync(Request())).RetryAtUtc);
    }

    [Fact]
    public async Task TransportFailuresAreRedactedAndCallerCancellationIsNotSwallowed()
    {
        var handler = new Handler { Failure = new HttpRequestException("private-provider-canary") }; using var http = new HttpClient(handler);
        var sender = new SendGridEmailSender(Options(), "Staging", http, new Clock());
        var result = await sender.SendAsync(Request()); Assert.Equal("email.provider_unavailable", result.Code); Assert.DoesNotContain("private-provider-canary", result.ToString());
        handler.Failure = new TaskCanceledException("private-timeout-canary");
        Assert.Equal(EmailStatus.ProviderUnavailable, (await sender.SendAsync(Request())).Status);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Request(), cancellation.Token)); Assert.Equal(2, handler.Calls);
    }
}
