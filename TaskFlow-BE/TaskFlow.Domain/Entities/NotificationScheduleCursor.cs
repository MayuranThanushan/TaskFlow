namespace TaskFlow.Domain.Entities;

public sealed class NotificationScheduleCursor
{
    public long WorkspaceId { get; set; }
    public long LastTaskId { get; set; }
    public DateTime? LastBatchAtUtc { get; set; }
}
