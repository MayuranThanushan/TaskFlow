namespace TaskFlow.Domain.Entities;
public sealed class TaskWatcher : IVersionedResource
{
    public long WorkspaceId { get; set; }
    public long ProjectId { get; set; }
    public long TaskId { get; set; }
    public long UserId { get; set; }
    public bool IsWatching { get; set; }
    public long Version { get; set; } = 1;
}
// Metadata only. Never render historical text or retarget a persisted source event.
public sealed class WatcherNotificationIntent
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long ProjectId { get; set; }
    public long TaskId { get; set; }
    public long RecipientUserId { get; set; }
    public long SubscriptionVersion { get; set; }
    public long AuthorityWatermark { get; set; }
    public long EventId { get; set; }
    public OutboxEvent Event { get; set; } = null!;
    public long? CommentId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public string State { get; set; } = "Pending";
    public string? Outcome { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
