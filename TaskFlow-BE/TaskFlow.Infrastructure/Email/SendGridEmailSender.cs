using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using TaskFlow.Application.Features.Email;

namespace TaskFlow.Infrastructure.Email;

public sealed class SendGridEmailOptions
{
    public string ApiKey { get; set; } = "";
    public string FromEmail { get; set; } = "";
    public string FromName { get; set; } = "TaskFlow";
    public override string ToString() => "SendGridEmailOptions [private configuration withheld]";
}

// An explicit staging adapter. No automatic HTTP retries or claim of recipient delivery.
public sealed class SendGridEmailSender(LocalEmailOptions options, string environment, HttpClient http, TimeProvider clock) : IEmailSender
{
    public static HttpClient CreateClient() => new(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<EmailResult> SendAsync(EmailRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (options.Mode != "SendGrid" || environment != "Staging") return new(EmailStatus.Disabled, "email.disabled");
        if (request is null || !EmailTemplates.ValidRequest(request)
            || request.Template == EmailTemplate.CompanyInvitation && request.InvitationId is null) return new(EmailStatus.InvalidRequest, "email.invalid");
        var settings = options.SendGrid;
        if (settings is null || string.IsNullOrWhiteSpace(settings.ApiKey) || settings.ApiKey.Length is < 20 or > 512
            || settings.ApiKey.Any(c => !char.IsAscii(c) || char.IsWhiteSpace(c) || char.IsControl(c))
            || !EmailTemplates.ValidRecipient(settings.FromEmail) || string.IsNullOrWhiteSpace(settings.FromName)
            || settings.FromName.Length > 80 || settings.FromName.Any(char.IsControl)
            || !Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out var origin) || origin.Scheme != "https"
            || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || options.AllowedRecipients is not { Length: > 0 and <= 100 } || options.AllowedRecipients.Any(r => !EmailTemplates.ValidRecipient(r)))
            return new(EmailStatus.Disabled, "email.configuration_invalid");
        if (!options.AllowedRecipients.Contains(request.Recipient, StringComparer.OrdinalIgnoreCase)) return new(EmailStatus.RecipientBlocked, "email.recipient_blocked");

        var body = EmailTemplates.Render(request, origin, localPreview: false);
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.sendgrid.com/v3/mail/send");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        message.Content = JsonContent.Create(new
        {
            personalizations = new[] { new { to = new[] { new { email = request.Recipient } } } },
            from = new { email = settings.FromEmail, name = settings.FromName },
            subject = body.Subject,
            content = new[] { new { type = "text/plain", value = body.Text }, new { type = "text/html", value = body.Html } },
            custom_args = new { taskflow_message_id = request.MessageId },
            tracking_settings = new { click_tracking = new { enable = false, enable_text = false }, open_tracking = new { enable = false }, subscription_tracking = new { enable = false } }
        });
        try
        {
            // Read headers only. Provider diagnostics can echo addresses, links and tokens.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode == HttpStatusCode.Accepted) return new(EmailStatus.ProviderAccepted, "email.provider_accepted");
            if ((int)response.StatusCode == 429)
            {
                var now = clock.GetUtcNow();
                var delay = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - now) ?? TimeSpan.FromSeconds(30);
                return new(EmailStatus.RateLimited, "email.provider_rate_limited", now.AddSeconds(Math.Clamp(delay.TotalSeconds, 5, 3600)));
            }
            if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout) return new(EmailStatus.ProviderUnavailable, "email.provider_unavailable");
            return new(EmailStatus.ProviderRejected, response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "email.provider_auth_failed" : "email.provider_rejected");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new(EmailStatus.ProviderUnavailable, "email.provider_unavailable"); }
        catch (HttpRequestException) { return new(EmailStatus.ProviderUnavailable, "email.provider_unavailable"); }
    }
}
