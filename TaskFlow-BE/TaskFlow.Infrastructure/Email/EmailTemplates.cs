using System.Net;
using System.Net.Mail;
using System.Text;
using TaskFlow.Application.Features.Email;

namespace TaskFlow.Infrastructure.Email;

public sealed class RenderedEmail(string subject, string text, string html)
{
    public string Subject { get; } = subject;
    public string Text { get; } = text;
    public string Html { get; } = html;
    public override string ToString() => "RenderedEmail [private fields withheld]";
}

public static class EmailTemplates
{
    private static bool Label(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum && !value.Any(char.IsControl);
    public static bool ValidRecipient(string? recipient)
    {
        if (!Label(recipient, 254) || recipient != recipient!.Trim()) return false;
        return MailAddress.TryCreate(recipient, out var address) && address.Address == recipient && address.DisplayName.Length == 0 && address.Host.Contains('.');
    }
    public static bool ValidRequest(EmailRequest request)
    {
        var account = request.Template is EmailTemplate.EmailVerification or EmailTemplate.PasswordRecovery;
        if (!Guid.TryParseExact(request.MessageId, "N", out _) || !ValidRecipient(request.Recipient) || !Label(request.DisplayName, 160) || !Enum.IsDefined(request.Template)) return false;
        if (account) return request.WorkspaceId == 0 && request.AccountUserId > 0 && request.SecurityToken is { Length: >= 20 and <= 4096 } && request.SecurityToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
        return request.WorkspaceId > 0 && request.AccountUserId is null && Label(request.CompanyName, 160) &&
            (request.Template != EmailTemplate.TaskAssigned || Label(request.TaskTitle, 200) && request.ProjectId > 0 && request.TaskId > 0) &&
            (request.Template != EmailTemplate.CompanyInvitation || request.InvitationToken is { Length: >= 20 and <= 512 } && request.InvitationToken.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
    }

    // Links are constructed from a deployment-controlled origin and closed route shapes.
    public static RenderedEmail Render(EmailRequest request, Uri origin)
    {
        if (!ValidRequest(request)) throw new ArgumentException("Invalid email request.");
        var (subject, message, path, action) = request.Template switch
        {
            EmailTemplate.EmailVerification => ("Verify your TaskFlow email", "Confirm your email address to protect your TaskFlow account. This link expires in one hour.", $"/verify-email#userId={request.AccountUserId}&token={Uri.EscapeDataString(request.SecurityToken)}", "Verify email"),
            EmailTemplate.PasswordRecovery => ("Reset your TaskFlow password", "A password reset was requested for your account. If this was not you, ignore this message. This link expires in one hour.", $"/reset-password#userId={request.AccountUserId}&token={Uri.EscapeDataString(request.SecurityToken)}", "Reset password"),
            EmailTemplate.CompanyInvitation => ("You are invited to TaskFlow", $"You have been invited to join {request.CompanyName}.", "/invitations/accept?token=" + Uri.EscapeDataString(request.InvitationToken), "Review invitation"),
            EmailTemplate.TaskAssigned => ("A task was assigned to you", $"A task in {request.CompanyName} was assigned to you: {request.TaskTitle}", $"/companies/{request.WorkspaceId}/tasks/{request.TaskId}", "View task"),
            EmailTemplate.WorkspaceWelcome => ("Welcome to your TaskFlow workspace", $"Your workspace, {request.CompanyName}, is ready.", $"/companies/{request.WorkspaceId}", "Open workspace"),
            _ => throw new ArgumentException("Unknown email template.")
        };
        var link = new Uri(origin, path).AbsoluteUri;
        var greeting = $"Hello {request.DisplayName},";
        var text = $"{greeting}\n\n{message}\n\n{action}: {link}\n\nTaskFlow\nThis is a local preview. No email has been sent.\n";
        static string E(string value) => WebUtility.HtmlEncode(value);
        var html = $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><title>{E(subject)}</title></head><body style=\"margin:0;background:#f4f6f8;color:#162c36;font:16px/1.6 Arial,sans-serif\"><main style=\"max-width:560px;margin:32px auto;background:#fff;padding:32px;border-radius:12px\"><p style=\"font-weight:bold;color:#225c4c\">TaskFlow</p><h1 style=\"font-size:24px\">{E(subject)}</h1><p>{E(greeting)}</p><p>{E(message)}</p><p><a href=\"{E(link)}\" style=\"color:#225c4c\">{E(action)}</a></p><hr><p style=\"font-size:13px;color:#52636b\">This is a local preview. No email has been sent.</p></main></body></html>";
        return new(subject, text, html);
    }
    internal static byte[] Mime(EmailRequest request, RenderedEmail body, DateTimeOffset now)
    {
        var id = Guid.ParseExact(request.MessageId, "N").ToString("N");
        var boundary = "taskflow_" + id;
        var scope = request.AccountUserId is { } account ? "account-" + account : request.WorkspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text), Base64FormattingOptions.InsertLineBreaks);
        var mime = $"From: TaskFlow <no-reply@taskflow.invalid>\r\nTo: {request.Recipient}\r\nMessage-ID: <{scope}.{id}@mail.taskflow.invalid>\r\nDate: {now.ToString("r", System.Globalization.CultureInfo.InvariantCulture)}\r\nSubject: =?utf-8?B?{B64(body.Subject)}?=\r\nMIME-Version: 1.0\r\nContent-Type: multipart/alternative; boundary=\"{boundary}\"\r\n\r\n--{boundary}\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{B64(body.Text)}\r\n--{boundary}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n{B64(body.Html)}\r\n--{boundary}--\r\n";
        return Encoding.UTF8.GetBytes(mime);
    }
}
