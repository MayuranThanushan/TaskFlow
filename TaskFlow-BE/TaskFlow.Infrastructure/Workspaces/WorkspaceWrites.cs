using System.Globalization;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Projects;

namespace TaskFlow.Infrastructure.Workspaces;

public sealed class WorkspaceWrites(IWorkspaceTransactions transactions, TimeProvider clock) : IWorkspaceWrites
{
    public async Task<ProjectDto> CreateProjectAsync(WorkspaceWriteContext context, string name, string? description, CancellationToken token = default) =>
        ProjectService.Map(await transactions.CreateAsync(context, (scope, _) =>
        { scope.Project.Name = name; scope.Project.Description = description; return Task.FromResult(scope.Project); }, token));

    public async Task<ProjectDto> UpdateProjectAsync(WorkspaceWriteContext context, long projectId, string name, string? description, CancellationToken token = default) =>
        ProjectService.Map(await transactions.ExecuteAsync(context, projectId, ProjectOperation.ManageProject, (scope, _) =>
        {
            name = TextRules.Required(name, 150, nameof(name)); description = TextRules.Optional(description, 10_000, nameof(description));
            if (scope.Project.Name != name || scope.Project.Description != description)
            { scope.Project.Name = name; scope.Project.Description = description; scope.Record(ActivityAction.ProjectUpdated); }
            return Task.FromResult(scope.Project);
        }, token));

    public async Task DeleteProjectAsync(WorkspaceWriteContext context, long projectId, CancellationToken token = default) =>
        await transactions.ExecuteAsync(context, projectId, ProjectOperation.ManageProject, (scope, _) =>
        { scope.Project.DeletedAtUtc = clock.GetUtcNow().UtcDateTime; scope.Record(ActivityAction.ProjectDeleted); return Task.FromResult(true); }, token);

    public async Task<ProjectMemberDto> AddMemberAsync(WorkspaceWriteContext context, long projectId, long userId, MembershipRole role, CancellationToken token = default)
    {
        var result = await transactions.ExecuteAsync(context, projectId, ProjectOperation.ManageMembers, async (scope, ct) =>
        {
            if (!Enum.IsDefined(role)) throw new ArgumentException("Invalid project role.");
            var added = await scope.AddMemberAsync(userId, ct); added.Member.Role = role; return added;
        }, token);
        return new(result.Member.UserId.ToString(CultureInfo.InvariantCulture), result.DisplayName, result.Member.Role.ToString(), result.Member.JoinedAtUtc);
    }
    public async Task RemoveMemberAsync(WorkspaceWriteContext context, long projectId, long userId, CancellationToken token = default) =>
        await transactions.ExecuteAsync(context, projectId, ProjectOperation.ManageMembers, async (scope, ct) => { await scope.RemoveMemberAsync(userId, ct); return true; }, token);
    public async Task<TaskDto> CreateTaskAsync(WorkspaceWriteContext context, long projectId, CreateTask request, CancellationToken token = default) =>
        TaskService.Map(await transactions.ExecuteAsync(context, projectId, ProjectOperation.WriteTasks, (scope, ct) => scope.CreateTaskAsync(request, ct), token));
    public async Task<TaskDto> UpdateTaskAsync(WorkspaceWriteContext context, long taskId, TaskPatch patch, CancellationToken token = default) =>
        TaskService.Map(await transactions.ExecuteTaskAsync(context, taskId, ProjectOperation.WriteTasks, (scope, ct) => scope.UpdateTaskAsync(taskId, patch, ct), token));
    public async Task<TaskStatusDto> SetStatusAsync(WorkspaceWriteContext context, long taskId, TaskState status, CancellationToken token = default)
    {
        var task = await transactions.ExecuteTaskAsync(context, taskId, ProjectOperation.WriteTasks, (scope, ct) => scope.SetTaskStatusAsync(taskId, status, ct), token);
        return new(task.Id.ToString(CultureInfo.InvariantCulture), task.Status.ToString(), task.UpdatedAtUtc);
    }
    public async Task DeleteTaskAsync(WorkspaceWriteContext context, long taskId, CancellationToken token = default) =>
        await transactions.ExecuteTaskAsync(context, taskId, ProjectOperation.DeleteTasks, async (scope, ct) => { await scope.DeleteTaskAsync(taskId, ct); return true; }, token);
}
