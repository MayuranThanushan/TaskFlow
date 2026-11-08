using System.Text;
using System.Text.Json;
using TaskFlow.Application.Features.Email;
using TaskFlow.Infrastructure.Email;

namespace TaskFlow.Tests;

public sealed class EmailTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "taskflow-email-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TestClock clock = new();
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 3, 12, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private LocalEmailOptions Options(int limit = 20) => new() { Mode = "LocalCapture", CaptureDirectory = root, PublicOrigin = "http://localhost:5173", PerCompanyHourlyLimit = limit };
    private LocalEmailSender Sender(LocalEmailOptions? options = null, string environment = "Development") => new(options ?? Options(), environment, clock);
    private static EmailRequest Request(string? id = null, long company = 7, string recipient = "alex@example.test", EmailTemplate template = EmailTemplate.CompanyInvitation, string name = "Alex", string token = "private-synthetic-invitation-token", string title = "Review release") => new()
    {
        WorkspaceId = company, MessageId = id ?? Guid.NewGuid().ToString("N"), Recipient = recipient, Template = template,
        DisplayName = name, CompanyName = "Example & Company", InvitationToken = token, TaskTitle = title, ProjectId = 11, TaskId = 12
    };
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData(EmailTemplate.CompanyInvitation)]
    [InlineData(EmailTemplate.TaskAssigned)]
    [InlineData(EmailTemplate.WorkspaceWelcome)]
    public async Task CapturesMultipartWithStableHeadersAndPrivateReceipt(EmailTemplate template)
    {
        var request = Request(template: template); var result = await Sender().SendAsync(request);
        Assert.Equal(EmailStatus.Captured, result.Status);
        var directory = Path.Combine(root, "7", request.MessageId);
        var mime = await File.ReadAllTextAsync(Path.Combine(directory, "message.eml"));
        Assert.Contains("multipart/alternative", mime); Assert.Contains("Content-Type: text/plain", mime); Assert.Contains("Content-Type: text/html", mime);
        Assert.Contains($"Message-ID: <7.{request.MessageId}@mail.taskflow.invalid>", mime);
        Assert.Contains("From: TaskFlow <no-reply@taskflow.invalid>", mime);
        var receipt = await File.ReadAllTextAsync(Path.Combine(directory, "receipt.json"));
        Assert.Contains("Captured", receipt); Assert.DoesNotContain(request.Recipient, receipt); Assert.DoesNotContain(request.InvitationToken, receipt); Assert.DoesNotContain(request.DisplayName, receipt);
        Assert.DoesNotContain(request.Recipient, request.ToString());
    }
    [Fact]
    public void TemplatesEscapeAllDynamicHtmlAndUseClosedTrustedLinks()
    {
        var request = Request(name: "<img src=x onerror=alert(1)>", title: "<script>bad()</script>", template: EmailTemplate.TaskAssigned);
        var rendered = EmailTemplates.Render(request, new Uri("https://preview.example.test"));
        Assert.DoesNotContain("<img", rendered.Html); Assert.DoesNotContain("<script>", rendered.Html);
        Assert.Contains("&lt;img", rendered.Html); Assert.Contains("&lt;script&gt;", rendered.Html);
        Assert.Contains("https://preview.example.test/companies/7/tasks/12", rendered.Text);
        Assert.Contains("<script>bad()</script>", rendered.Text); Assert.DoesNotContain("onerror", rendered.ToString());
        Assert.Contains("No email has been sent", rendered.Html);
    }
    [Theory]
    [InlineData("alex@example.test\r\nBcc: victim@example.test")]
    [InlineData("Name <alex@example.test>")]
    [InlineData("alex@example.test,other@example.test")]
    [InlineData(" alex@example.test")]
    [InlineData("no-email")]
    public async Task InvalidAddressesCannotInjectHeadersOrCreateFiles(string recipient)
    {
        Assert.Equal(EmailStatus.InvalidRequest, (await Sender().SendAsync(Request(recipient: recipient))).Status);
        Assert.False(Directory.Exists(root));
    }
    [Theory]
    [InlineData("Production")]
    [InlineData("UAT")]
    [InlineData("Unknown")]
    public async Task UnsupportedEnvironmentsFailClosed(string environment)
    {
        Assert.Equal(EmailStatus.Disabled, (await Sender(environment: environment).SendAsync(Request())).Status); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task DisabledDefaultAndUnapprovedRecipientsDoNotWrite()
    {
        Assert.Equal(EmailStatus.Disabled, (await Sender(new()).SendAsync(Request())).Status);
        Assert.Equal(EmailStatus.RecipientBlocked, (await Sender().SendAsync(Request(recipient: "someone@example.com"))).Status);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task StagingRequiresHttpsAndExplicitExactRecipientAllowlist()
    {
        var options = Options(); options.PublicOrigin = "https://staging.example.test";
        Assert.Equal(EmailStatus.RecipientBlocked, (await Sender(options, "Staging").SendAsync(Request())).Status);
        options.AllowedRecipients = ["alex@example.test"];
        Assert.Equal(EmailStatus.Captured, (await Sender(options, "Staging").SendAsync(Request())).Status);
        Assert.Equal(EmailStatus.RecipientBlocked, (await Sender(options, "Staging").SendAsync(Request(recipient: "alex+other@example.test"))).Status);
        options.PublicOrigin = "http://localhost:5173";
        Assert.Equal(EmailStatus.Disabled, (await Sender(options, "Staging").SendAsync(Request())).Status);
    }
    [Theory]
    [InlineData("http://external.example.test")]
    [InlineData("https://user:secret@example.test")]
    [InlineData("https://example.test/path")]
    [InlineData("https://example.test/?next=evil")]
    [InlineData("https://example.test/#fragment")]
    [InlineData("javascript:alert(1)")]
    public async Task InvalidOriginsCannotEnterTemplates(string origin)
    {
        var options = Options(); options.PublicOrigin = origin;
        Assert.Equal(EmailStatus.Disabled, (await Sender(options).SendAsync(Request())).Status); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task InvalidScopeIdentityTemplateAndTokenAreRejected()
    {
        foreach (var request in new[] { Request(company: 0), Request(id: "../../outside"), Request(template: (EmailTemplate)999), Request(token: "https://evil.test"), Request(name: "Name\nHeader"), Request(title: new string('x', 201), template: EmailTemplate.TaskAssigned) })
            Assert.Equal(EmailStatus.InvalidRequest, (await Sender().SendAsync(request)).Status);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task RetryAfterRestartReusesReceiptButChangedRecipientOrContentConflicts()
    {
        var request = Request(); Assert.Equal(EmailStatus.Captured, (await Sender().SendAsync(request)).Status);
        var file = Path.Combine(root, "7", request.MessageId, "message.eml"); var before = await File.ReadAllBytesAsync(file);
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(EmailStatus.AlreadyCaptured, (await Sender().SendAsync(request)).Status);
        Assert.Equal(before, await File.ReadAllBytesAsync(file));
        Assert.Equal(EmailStatus.IdConflict, (await Sender().SendAsync(Request(request.MessageId, recipient: "other@example.test"))).Status);
        Assert.Equal(EmailStatus.IdConflict, (await Sender().SendAsync(Request(request.MessageId, name: "Changed"))).Status);
        Assert.Equal(EmailStatus.Captured, (await Sender().SendAsync(Request(request.MessageId, company: 8))).Status);
    }
    [Fact]
    public async Task QuotaPersistsAcrossSendersAndRetriesDoNotConsumeIt()
    {
        var request = Request(); Assert.True((await Sender(Options(1)).SendAsync(request)).IsCaptured);
        Assert.Equal(EmailStatus.AlreadyCaptured, (await Sender(Options(1)).SendAsync(request)).Status);
        var limited = await Sender(Options(1)).SendAsync(Request()); Assert.Equal(EmailStatus.RateLimited, limited.Status); Assert.Equal(new DateTimeOffset(2026, 11, 3, 13, 0, 0, TimeSpan.Zero), limited.RetryAtUtc);
        Assert.True((await Sender(Options(1)).SendAsync(Request(company: 8))).IsCaptured);
        clock.Now = clock.Now.AddMinutes(30); Assert.True((await Sender(Options(1)).SendAsync(Request())).IsCaptured);
    }
    [Fact]
    public async Task IndependentSendersCannotExceedCompanyLimit()
    {
        var sends = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Sender(Options(1)).SendAsync(Request())));
        Assert.Single(sends, x => x.Status == EmailStatus.Captured);
        Assert.All(sends.Where(x => x.Status != EmailStatus.Captured), x => Assert.Contains(x.Status, new[] { EmailStatus.RateLimited, EmailStatus.CaptureUnavailable }));
        Assert.Single(Directory.EnumerateDirectories(Path.Combine(root, "7")), p => !Path.GetFileName(p).StartsWith('.'));
    }
    [Fact]
    public async Task LockedSinkAndCorruptReceiptFailSafelyWithoutLosingOriginal()
    {
        var request = Request(); Assert.True((await Sender().SendAsync(request)).IsCaptured);
        using (var gate = new FileStream(Path.Combine(root, "7", ".capture.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(EmailStatus.CaptureUnavailable, (await Sender().SendAsync(Request())).Status);
        var directory = Path.Combine(root, "7", request.MessageId);
        await File.AppendAllTextAsync(Path.Combine(directory, "message.eml"), "tamper");
        Assert.Equal(EmailStatus.CaptureUnavailable, (await Sender().SendAsync(request)).Status);
        await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), "corrupt private-provider-message");
        var result = await Sender().SendAsync(request); Assert.Equal(EmailStatus.CaptureUnavailable, result.Status); Assert.DoesNotContain("private-provider", result.ToString());
    }
    [Fact]
    public async Task CancellationLeavesNoCommittedPartialCapture()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Sender().SendAsync(Request(), cancellation.Token)); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task InvalidCaptureConfigurationCannotWriteIntoWebRoot()
    {
        var options = Options(); options.CaptureDirectory = Path.Combine(root, "wwwroot", "mail");
        Assert.Equal(EmailStatus.Disabled, (await Sender(options).SendAsync(Request())).Status);
        options.CaptureDirectory = "relative-path"; Assert.Equal(EmailStatus.Disabled, (await Sender(options).SendAsync(Request())).Status);
        options = Options(0); Assert.Equal(EmailStatus.Disabled, (await Sender(options).SendAsync(Request())).Status); Assert.False(Directory.Exists(root));
    }
    [Fact]
    public async Task CliProducesSafeMachineReadableStatusWithoutDatabaseOrPrivateFields()
    {
        Directory.CreateDirectory(root); var input = Path.Combine(root, "request.json"); var request = Request();
        var json = JsonSerializer.Serialize(new { request.WorkspaceId, request.MessageId, request.Recipient, template = "CompanyInvitation", request.DisplayName, request.CompanyName, request.InvitationToken });
        await File.WriteAllTextAsync(input, json); using var output = new StringWriter();
        Assert.Equal(0, await EmailCaptureCommand.RunAsync(input, Sender(), output)); Assert.Contains("email.captured", output.ToString());
        Assert.DoesNotContain(request.Recipient, output.ToString()); Assert.DoesNotContain(request.InvitationToken, output.ToString()); Assert.DoesNotContain(root, output.ToString());
        await File.WriteAllTextAsync(input, json[..^1] + ",\"unexpected\":true}");
        Assert.Equal(2, await EmailCaptureCommand.RunAsync(input, Sender(), new StringWriter()));
        await File.WriteAllTextAsync(input, new string('x', 16_385)); Assert.Equal(2, await EmailCaptureCommand.RunAsync(input, Sender(), new StringWriter()));
    }
}
