using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Application.Features.Workspaces;

public readonly record struct WorkspaceWriteContext(long ActorId, long WorkspaceId);
public interface IWorkspaceTransactions
{
    Task<T> CreateAsync<T>(WorkspaceWriteContext context, Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken token = default);
    Task<T> ExecuteAsync<T>(WorkspaceWriteContext context, long projectId, ProjectOperation operation, Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken token = default);
    Task<T> ExecuteTaskAsync<T>(WorkspaceWriteContext context, long taskId, ProjectOperation operation, Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken token = default);
}
public interface IWorkspaceWrites
{
    Task<ProjectDto> CreateProjectAsync(WorkspaceWriteContext context, string name, string? description, CancellationToken token = default);
    Task<ProjectDto> UpdateProjectAsync(WorkspaceWriteContext context, long projectId, string name, string? description, CancellationToken token = default);
    Task DeleteProjectAsync(WorkspaceWriteContext context, long projectId, CancellationToken token = default);
    Task<ProjectMemberDto> AddMemberAsync(WorkspaceWriteContext context, long projectId, long userId, CancellationToken token = default);
    Task RemoveMemberAsync(WorkspaceWriteContext context, long projectId, long userId, CancellationToken token = default);
    Task<TaskDto> CreateTaskAsync(WorkspaceWriteContext context, long projectId, CreateTask request, CancellationToken token = default);
    Task<TaskDto> UpdateTaskAsync(WorkspaceWriteContext context, long taskId, TaskPatch patch, CancellationToken token = default);
    Task<TaskStatusDto> SetStatusAsync(WorkspaceWriteContext context, long taskId, TaskState status, CancellationToken token = default);
    Task DeleteTaskAsync(WorkspaceWriteContext context, long taskId, CancellationToken token = default);
}
