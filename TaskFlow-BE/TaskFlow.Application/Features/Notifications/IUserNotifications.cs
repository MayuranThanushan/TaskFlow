using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Application.Features.Notifications;

public sealed record NotificationDto(string Id, string WorkspaceId, string Kind, string ProjectId, string ProjectName, string TaskId, string TaskTitle, DateTime CreatedAtUtc, DateTime? ReadAtUtc);
public sealed record NotificationPage(NotificationDto[] Items, int Page, int PageSize, int TotalCount, int TotalPages, int UnreadCount);
public sealed record NotificationCountDto(int UnreadCount);
public interface IUserNotifications
{
    Task<NotificationPage> ListAsync(WorkspaceReadContext context, int page, int pageSize, bool unreadOnly, CancellationToken token = default);
    Task<NotificationCountDto> CountAsync(WorkspaceReadContext context, CancellationToken token = default);
    Task SetReadAsync(WorkspaceReadContext context, long notificationId, bool isRead, CancellationToken token = default);
}
