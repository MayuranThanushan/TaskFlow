using TaskFlow.Application.Features.Activity;
using TaskFlow.Application.Features.Dashboard;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;

namespace TaskFlow.Application.Features.Workspaces;

public readonly record struct WorkspaceReadContext(long ActorId, long WorkspaceId);
public sealed record WorkspaceDto(string Id, string Name, string Role);
public sealed record WorkspacePage(IReadOnlyList<WorkspaceDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public interface IWorkspaceReads
{
    Task AuthorizeAsync(WorkspaceReadContext context, CancellationToken token = default);
    Task<WorkspacePage> WorkspacesAsync(long actorId, int page, int pageSize, CancellationToken token = default);
    Task<WorkspaceDto> WorkspaceAsync(WorkspaceReadContext context, CancellationToken token = default);
    Task<ProjectPage> ProjectsAsync(WorkspaceReadContext context, ProjectListOptions options, CancellationToken token = default);
    Task<ProjectDetailsDto> ProjectAsync(WorkspaceReadContext context, long projectId, CancellationToken token = default);
    Task<TaskPage> TasksAsync(WorkspaceReadContext context, long projectId, TaskListOptions options, CancellationToken token = default);
    Task<TaskDto> TaskAsync(WorkspaceReadContext context, long taskId, CancellationToken token = default);
    Task<ProjectMemberList> MembersAsync(WorkspaceReadContext context, long projectId, CancellationToken token = default);
    Task<ActivityPage> ActivityAsync(WorkspaceReadContext context, long? projectId, ActivityOptions options, CancellationToken token = default);
    Task<DashboardDto> DashboardAsync(WorkspaceReadContext context, CancellationToken token = default);
}
