using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Projects;

public sealed class TaskService(TaskFlowDbContext database, IProjectTransactions transactions) : ITaskService
{
    private async Task<long> ParentAsync(long? actorId, long taskId, CancellationToken cancellationToken)
    {
        var actor = Actor(actorId);
        return await Visible(actor).Where(t => t.Id == taskId).Select(t => (long?)t.ProjectId).SingleOrDefaultAsync(cancellationToken)
            ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    }
    public async Task<TaskDto> UpdateAsync(long? actorId, long taskId, TaskPatch patch, CancellationToken cancellationToken = default)
    {
        if (!patch.Title.IsSpecified && !patch.Description.IsSpecified && !patch.Priority.IsSpecified && !patch.AssigneeId.IsSpecified && !patch.DueDateUtc.IsSpecified)
            throw new TaskValidationException("body", "Supply at least one editable field.");
        var parent = await ParentAsync(actorId, taskId, cancellationToken);
        return Map(await transactions.ExecuteAsync(actorId, parent, ProjectOperation.WriteTasks, (scope, ct) => scope.UpdateTaskAsync(taskId, patch, ct), cancellationToken));
    }
    public async Task<TaskStatusDto> SetStatusAsync(long? actorId, long taskId, TaskState status, CancellationToken cancellationToken = default)
    {
        var parent = await ParentAsync(actorId, taskId, cancellationToken);
        var task = await transactions.ExecuteAsync(actorId, parent, ProjectOperation.WriteTasks, (scope, ct) => scope.SetTaskStatusAsync(taskId, status, ct), cancellationToken);
        return new(task.Id.ToString(CultureInfo.InvariantCulture), task.Status.ToString(), task.UpdatedAtUtc);
    }
    public async Task DeleteAsync(long? actorId, long taskId, CancellationToken cancellationToken = default)
    {
        var parent = await ParentAsync(actorId, taskId, cancellationToken);
        await transactions.ExecuteAsync(actorId, parent, ProjectOperation.DeleteTasks, async (scope, ct) => { await scope.DeleteTaskAsync(taskId, ct); return true; }, cancellationToken);
    }
    public async Task<TaskDto> CreateAsync(long? actorId, long projectId, CreateTask request, CancellationToken cancellationToken = default)
    {
        var task = await transactions.ExecuteAsync(actorId, projectId, ProjectOperation.WriteTasks,
            (scope, ct) => scope.CreateTaskAsync(request, ct), cancellationToken);
        return Map(task);
    }
    public async Task<TaskDto> GetAsync(long? actorId, long taskId, CancellationToken cancellationToken = default)
    {
        var actor = Actor(actorId);
        var task = await Visible(actor).SingleOrDefaultAsync(t => t.Id == taskId, cancellationToken)
            ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        return Map(task);
    }
    public async Task<TaskPage> ListAsync(long? actorId, long projectId, TaskListOptions options, CancellationToken cancellationToken = default)
    {
        var actor = Actor(actorId);
        if (options.Page < 1 || options.PageSize is < 1 or > 100 || (long)(options.Page - 1) * options.PageSize > int.MaxValue
            || options.Search?.Length > 200 || options.SortBy is not ("title" or "priority" or "dueDateUtc" or "createdAtUtc")
            || options.SortDirection is not ("asc" or "desc") || options.Status is { } s && !Enum.IsDefined(s)
            || options.Priority is { } p && !Enum.IsDefined(p) || options.AssigneeId is <= 0) throw new ArgumentException("Invalid task list options.");
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (!await ProjectAccess.VisibleProjects(database, actor).AnyAsync(p => p.Id == projectId, cancellationToken))
            throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        var query = Visible(actor).Where(t => t.ProjectId == projectId);
        var search = options.Search?.Trim(); if (!string.IsNullOrEmpty(search)) query = query.Where(t => t.Title.Contains(search));
        if (options.Status is { } status) query = query.Where(t => t.Status == status);
        if (options.Priority is { } priority) query = query.Where(t => t.Priority == priority);
        if (options.AssigneeId is { } assignee) query = query.Where(t => t.AssigneeId == assignee);
        var total = await query.CountAsync(cancellationToken); var asc = options.SortDirection == "asc";
        var ordered = options.SortBy switch
        {
            "title" => asc ? query.OrderBy(t => t.Title) : query.OrderByDescending(t => t.Title),
            "priority" => asc ? query.OrderBy(t => t.Priority == TaskPriority.Low ? 0 : t.Priority == TaskPriority.Medium ? 1 : 2) : query.OrderByDescending(t => t.Priority == TaskPriority.Low ? 0 : t.Priority == TaskPriority.Medium ? 1 : 2),
            "dueDateUtc" => asc ? query.OrderBy(t => t.DueDateUtc == null).ThenBy(t => t.DueDateUtc) : query.OrderBy(t => t.DueDateUtc == null).ThenByDescending(t => t.DueDateUtc),
            _ => asc ? query.OrderBy(t => t.CreatedAtUtc) : query.OrderByDescending(t => t.CreatedAtUtc)
        };
        var tasks = await ordered.ThenByDescending(t => t.Id).Skip((options.Page - 1) * options.PageSize).Take(options.PageSize).ToListAsync(cancellationToken);
        await snapshot.CommitAsync(cancellationToken);
        return new(tasks.Select(Map).ToArray(), options.Page, options.PageSize, total, (int)Math.Ceiling(total / (double)options.PageSize));
    }
    private IQueryable<TaskItem> Visible(long actor)
    {
        var projects = ProjectAccess.VisibleProjects(database, actor);
        return database.Tasks.AsNoTracking().Where(t => projects.Any(p => p.Id == t.ProjectId));
    }
    private static long Actor(long? actor) => actor is > 0 ? actor.Value : throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
    internal static TaskDto Map(TaskItem t) => new(t.Id.ToString(CultureInfo.InvariantCulture), t.ProjectId.ToString(CultureInfo.InvariantCulture), t.Title, t.Description,
        t.Status.ToString(), t.Priority.ToString(), t.AssigneeId?.ToString(CultureInfo.InvariantCulture), t.DueDateUtc, t.CreatedAtUtc, t.UpdatedAtUtc);
}
