using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Application.Features.Notifications;

public sealed record NotificationDto(string Id, string WorkspaceId, string Kind, string ProjectId, string ProjectName, string TaskId, string TaskTitle, DateTime CreatedAtUtc, DateTime? ReadAtUtc, string DeliveryStatus = "InAppDelivered");
public sealed record NotificationPage(NotificationDto[] Items, int Page, int PageSize, int TotalCount, int TotalPages, int UnreadCount);
public sealed record NotificationCountDto(int UnreadCount);
public sealed record NotificationPreferencesDto(bool InAppEnabled, bool TaskAssignedEnabled, bool EmailAvailable, string Version, bool DueReminderEnabled = false, bool DailyDigestEnabled = false, string TimeZoneId = "UTC", int DigestHour = 9, string WatcherDeliveryMode = "Immediate", int? QuietStartHour = null, int? QuietEndHour = null);
public sealed record NotificationNoiseOptions(string WatcherDeliveryMode, int? QuietStartHour, int? QuietEndHour);
public sealed record NotificationGroupDto(string TaskId, string ProjectId, string TaskTitle, string ProjectName, string LatestNotificationId, DateTime LatestAtUtc, int NotificationCount, int UnreadCount);
public sealed record NotificationGroupPage(NotificationGroupDto[] Items, int Page, int PageSize, int TotalCount, int TotalPages, int UnreadCount);
public sealed record NotificationScheduleOptions(bool DueReminderEnabled, bool DailyDigestEnabled, string TimeZoneId, int DigestHour);
public interface IUserNotifications
{
    Task<NotificationPage> ListAsync(WorkspaceReadContext context, int page, int pageSize, bool unreadOnly, CancellationToken token = default, long? taskId = null);
    Task<NotificationGroupPage> GroupsAsync(WorkspaceReadContext context, int page, int pageSize, bool unreadOnly, CancellationToken token = default);
    Task<NotificationCountDto> CountAsync(WorkspaceReadContext context, CancellationToken token = default);
    Task SetReadAsync(WorkspaceReadContext context, long notificationId, bool isRead, CancellationToken token = default);
    Task MarkReadAsync(WorkspaceReadContext context, IReadOnlyList<long> ids, CancellationToken token = default);
    Task<NotificationPreferencesDto> PreferencesAsync(WorkspaceReadContext context, CancellationToken token = default);
    Task<NotificationPreferencesDto> SetPreferencesAsync(WorkspaceReadContext context, bool inAppEnabled, bool taskAssignedEnabled, long expectedVersion, CancellationToken token = default, NotificationScheduleOptions? schedule = null, NotificationNoiseOptions? noise = null);
}
