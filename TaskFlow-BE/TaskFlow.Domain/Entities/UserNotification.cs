namespace TaskFlow.Domain.Entities;

// Recipient-bound in-app pointer, never a stored title/body or email delivery claim.
public sealed class UserNotification
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long RecipientUserId { get; set; }
    public long? EventId { get; set; }
    public string? ScheduleKey { get; set; }
    public DateTime? SourceDueDateUtc { get; set; }
    public OutboxEvent Event { get; set; } = null!;
    public long ProjectId { get; set; }
    public long TaskId { get; set; }
    public string Kind { get; set; } = "TaskAssigned";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
