namespace TaskFlow.Application.Features.Email;

public enum EmailTemplate { CompanyInvitation = 1, TaskAssigned = 2, WorkspaceWelcome = 3, EmailVerification = 4, PasswordRecovery = 5 }
public enum EmailStatus { Captured, AlreadyCaptured, Disabled, InvalidRequest, RecipientBlocked, RateLimited, IdConflict, CaptureUnavailable }

// An explicit, recipient-bound request. Never create these by replaying checkpoint facts.
public sealed class EmailRequest
{
    public long? InvitationId { get; init; }
    public long? AccountUserId { get; init; }
    public string SecurityToken { get; init; } = "";
    public long WorkspaceId { get; init; }
    public string MessageId { get; init; } = "";
    public string Recipient { get; init; } = "";
    public EmailTemplate Template { get; init; }
    public string DisplayName { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string TaskTitle { get; init; } = "";
    public long? ProjectId { get; init; }
    public long? TaskId { get; init; }
    public string InvitationToken { get; init; } = "";
    public override string ToString() => "EmailRequest [private fields withheld]";
}

// Captured is a local artifact acknowledgement, never Delivered or provider acceptance.
public sealed record EmailResult(EmailStatus Status, string Code, DateTimeOffset? RetryAtUtc = null)
{
    public bool IsCaptured => Status is EmailStatus.Captured or EmailStatus.AlreadyCaptured;
}

public interface IEmailSender
{
    Task<EmailResult> SendAsync(EmailRequest request, CancellationToken token = default);
}
