using TaskFlow.Application.Features.Workspaces;
namespace TaskFlow.Application.Features.Notifications;
public sealed record TaskWatcherDto(bool IsWatching, string Version);
public interface ITaskWatchers
{
    Task<TaskWatcherDto> GetAsync(WorkspaceReadContext context, long taskId, CancellationToken token = default);
    Task<TaskWatcherDto> SetAsync(WorkspaceReadContext context, long taskId, bool isWatching, long expectedVersion, CancellationToken token = default);
}
