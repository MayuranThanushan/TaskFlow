namespace TaskFlow.Domain.Entities;

public sealed class NotificationPreference : IVersionedResource
{
    public long WorkspaceId { get; set; }
    public long UserId { get; set; }
    public bool InAppEnabled { get; set; } = true;
    public bool TaskAssignedEnabled { get; set; } = true;
    public bool DueReminderEnabled { get; set; }
    public bool DailyDigestEnabled { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public int DigestHour { get; set; } = 9;
    public long Version { get; set; } = 1;
}
