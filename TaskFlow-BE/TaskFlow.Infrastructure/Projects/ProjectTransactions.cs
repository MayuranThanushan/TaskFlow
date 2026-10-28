using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Projects;

public sealed class ProjectTransactions(IServiceScopeFactory scopeFactory) : IProjectTransactions, IWorkspaceTransactions
{
    public Task<T> CreateAsync<T>(WorkspaceWriteContext context, Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken token = default) =>
        RunAsync(context.ActorId, null, ProjectOperation.ManageProject, mutation, token, context);
    public Task<T> ExecuteAsync<T>(WorkspaceWriteContext context, long projectId, ProjectOperation operation, Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken token = default)
    {
        if (!Enum.IsDefined(operation) || operation == ProjectOperation.Read) throw new ArgumentOutOfRangeException(nameof(operation));
        return RunAsync(context.ActorId, projectId, operation, mutation, token, context);
    }
    public Task<T> ExecuteTaskAsync<T>(WorkspaceWriteContext context, long taskId, ProjectOperation operation, Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken token = default)
    {
        if (operation is not (ProjectOperation.WriteTasks or ProjectOperation.DeleteTasks)) throw new ArgumentOutOfRangeException(nameof(operation));
        return RunAsync(context.ActorId, null, operation, mutation, token, context, taskId);
    }
    public Task<T> CreateAsync<T>(long? actorId, Func<IProjectMutation, CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken = default) => RunAsync(actorId, null, ProjectOperation.ManageProject, mutation, cancellationToken);

    public Task<T> ExecuteAsync<T>(long? actorId, long projectId, ProjectOperation operation,
        Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(operation) || operation == ProjectOperation.Read)
            throw new ArgumentOutOfRangeException(nameof(operation), "Use the access service for reads.");
        return RunAsync(actorId, projectId, operation, mutation, cancellationToken);
    }

    private async Task<T> RunAsync<T>(long? actorId, long? projectId, ProjectOperation operation,
        Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken cancellationToken,
        WorkspaceWriteContext? workspace = null, long? scopedTaskId = null)
    {
        if (actorId is null or <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        // A fresh scope keeps old tracked entities, snapshots and failed writes out of this boundary.
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (workspace is { } context)
        {
            // Conservative workspace gate: all scoped writers/lifecycle operations lock this first.
            // The actor row is also locked, so direct concurrent membership revocation cannot race admission.
            var companies = await database.Workspaces.FromSqlInterpolated($"SELECT * FROM `Workspaces` WHERE `Id` = {context.WorkspaceId} FOR UPDATE").ToListAsync(cancellationToken);
            if (context.WorkspaceId <= 0 || companies.Count != 1) throw new ProjectAccessException(ProjectAccessDecision.NotFound);
            var memberships = await database.WorkspaceMemberships.FromSqlInterpolated($"SELECT * FROM `WorkspaceMemberships` WHERE `WorkspaceId` = {context.WorkspaceId} AND `UserId` = {context.ActorId} FOR UPDATE").AsNoTracking().ToListAsync(cancellationToken);
            var membership = memberships.SingleOrDefault();
            if (membership?.State != WorkspaceMembershipState.Active) throw new ProjectAccessException(ProjectAccessDecision.NotFound);
            if (projectId is null && scopedTaskId is null && membership.Role == WorkspaceRole.Guest)
                throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
            if (scopedTaskId is { } task)
                projectId = await database.Tasks.AsNoTracking().Where(t => t.Id == task && t.WorkspaceId == context.WorkspaceId
                    && t.Project.WorkspaceId == context.WorkspaceId && t.DeletedAtUtc == null && t.Project.DeletedAtUtc == null)
                    .Select(t => (long?)t.ProjectId).SingleOrDefaultAsync(cancellationToken)
                    ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        }
        Project project;
        if (projectId is not null)
        {
            // Materialize directly: composing Single/Where over FOR UPDATE can change MySQL locking semantics.
            var locked = workspace is { } projectContext
                ? await database.Projects.FromSqlInterpolated($"SELECT * FROM `Projects` WHERE `Id` = {projectId.Value} AND `WorkspaceId` = {projectContext.WorkspaceId} FOR UPDATE")
                    .IgnoreQueryFilters().ToListAsync(cancellationToken)
                : await database.Projects.FromSqlInterpolated($"SELECT * FROM `Projects` WHERE `Id` = {projectId.Value} FOR UPDATE")
                    .IgnoreQueryFilters().ToListAsync(cancellationToken);
            project = locked.SingleOrDefault() ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
            ProjectRole? role;
            if (workspace is { } tenant)
            {
                if (project.WorkspaceId != tenant.WorkspaceId || project.DeletedAtUtc != null
                    || !await database.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == tenant.WorkspaceId && m.UserId == project.OwnerId, cancellationToken))
                    throw new ProjectAccessException(ProjectAccessDecision.NotFound);
                var grant = await database.ProjectMembers.AsNoTracking().SingleOrDefaultAsync(m => m.WorkspaceId == tenant.WorkspaceId && m.ProjectId == project.Id && m.UserId == actorId && m.RevokedAtUtc == null, cancellationToken);
                role = project.OwnerId == actorId ? ProjectRole.Owner : grant is null ? null : grant.Role == MembershipRole.Viewer ? ProjectRole.Viewer : ProjectRole.Member;
            }
            else role = await ProjectAccess.RoleAsync(database, actorId.Value, project.Id, cancellationToken);
            var decision = ProjectAccessPolicy.Decide(actorId, role, operation);
            if (decision != ProjectAccessDecision.Allowed) throw new ProjectAccessException(decision);
        }
        else
        {
            project = new Project { OwnerId = actorId.Value, WorkspaceId = workspace?.WorkspaceId };
            database.Projects.Add(project);
        }

        var originalId = project.Id;
        var ownerId = project.OwnerId;
        var workspaceId = project.WorkspaceId;
        var boundary = new Mutation(database, project, operation, scope.ServiceProvider.GetRequiredService<TimeProvider>(), workspace?.WorkspaceId);
        if (projectId is null) boundary.Record(ActivityAction.ProjectCreated);
        var result = await mutation(boundary, cancellationToken);
        if (project.Id != originalId || project.OwnerId != ownerId || project.WorkspaceId != workspaceId)
            throw new InvalidOperationException("Project identity and ownership cannot be changed.");
        project.Name = TextRules.Required(project.Name, 150, nameof(project.Name));
        project.Description = TextRules.Optional(project.Description, 10_000, nameof(project.Description));
        database.ChangeTracker.DetectChanges();
        if (projectId is not null && operation != ProjectOperation.ManageProject
            && database.Entry(project).State != EntityState.Unchanged)
            throw new InvalidOperationException("Project changes require the project management operation.");
        var changed = database.ChangeTracker.HasChanges();
        if (changed != (boundary.Events.Count > 0))
            throw new InvalidOperationException("A mutation and its activity must be recorded together; no-op activity is forbidden.");
        if (changed)
        {
            // Persist generated IDs first; both saves remain inside the same transaction.
            await database.SaveChangesAsync(cancellationToken);
            foreach (var activity in boundary.Events)
            {
                var eventTaskId = activity.Task?.Id ?? activity.TaskId;
                if (eventTaskId is { } taskId && !await database.Tasks.IgnoreQueryFilters()
                    .AnyAsync(task => task.Id == taskId && task.ProjectId == project.Id && (workspace == null || task.WorkspaceId == workspaceId), cancellationToken))
                    throw new InvalidOperationException("Activity task must belong to the locked project.");
                if (workspace is { } auditContext && activity.Action is ActivityAction.MemberAdded or ActivityAction.MemberRemoved)
                {
                    var data = JsonSerializer.Deserialize<Dictionary<string, string?>>(activity.Details!);
                    if (data is not null && data.TryGetValue("userId", out var raw) && long.TryParse(raw, out var target))
                    {
                        var grantRole = database.ChangeTracker.Entries<ProjectMember>().Where(e => e.Entity.ProjectId == project.Id && e.Entity.UserId == target).Select(e => e.Entity.Role.ToString()).SingleOrDefault();
                        database.WorkspaceAuditLogs.Add(new WorkspaceAuditLog { WorkspaceId = auditContext.WorkspaceId, ActorUserId = auditContext.ActorId, TargetUserId = target, ProjectId = project.Id, Action = activity.Action == ActivityAction.MemberAdded ? "ProjectMemberAdded" : "ProjectMemberRemoved", FromRole = activity.Action == ActivityAction.MemberRemoved ? grantRole : null, ToRole = activity.Action == ActivityAction.MemberAdded ? grantRole : null });
                    }
                }
                database.ActivityLogs.Add(new ActivityLog
                {
                    ProjectId = project.Id, WorkspaceId = workspace?.WorkspaceId, ActorUserId = actorId.Value, TaskId = eventTaskId,
                    ActionType = activity.Action, Details = activity.Details
                });
            }
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private sealed record PendingActivity(ActivityAction Action, string? Details, long? TaskId, TaskItem? Task = null);

    private sealed class Mutation(TaskFlowDbContext database, Project project, ProjectOperation operation, TimeProvider clock, long? workspaceId) : IProjectMutation
    {
        public Project Project => project;
        public List<PendingActivity> Events { get; } = [];

        private async Task<TaskItem> TaskForWriteAsync(long taskId, ProjectOperation expected, CancellationToken cancellationToken)
        {
            if (operation != expected) throw new InvalidOperationException("Incorrect task mutation operation.");
            return await database.Tasks.SingleOrDefaultAsync(t => t.Id == taskId && t.ProjectId == project.Id && (workspaceId == null || t.WorkspaceId == workspaceId), cancellationToken)
                ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        }

        public async Task<TaskItem> UpdateTaskAsync(long taskId, TaskFlow.Application.Features.Tasks.TaskPatch patch, CancellationToken cancellationToken = default)
        {
            var task = await TaskForWriteAsync(taskId, ProjectOperation.WriteTasks, cancellationToken);
            var title = patch.Title.IsSpecified ? TextRules.Required(patch.Title.Value!, 200, "title") : task.Title;
            var description = patch.Description.IsSpecified ? TextRules.Optional(patch.Description.Value, 10_000, "description") : task.Description;
            var priority = patch.Priority.IsSpecified ? patch.Priority.Value : task.Priority;
            var assignee = patch.AssigneeId.IsSpecified ? patch.AssigneeId.Value : task.AssigneeId;
            var due = patch.DueDateUtc.IsSpecified ? patch.DueDateUtc.Value : task.DueDateUtc;
            if (!Enum.IsDefined(priority)) throw new ArgumentException("Unsupported priority.");
            if (assignee is { } user && !await IsEligibleAssigneeAsync(user, cancellationToken))
                throw new TaskFlow.Application.Features.Tasks.TaskValidationException("assigneeId", "Choose the project owner or a current member.");
            var edited = title != task.Title || description != task.Description || priority != task.Priority || due != task.DueDateUtc;
            if (edited) Record(ActivityAction.TaskUpdated, taskId: task.Id);
            if (assignee != task.AssigneeId)
            {
                if (task.AssigneeId is { } previous) Record(ActivityAction.TaskUnassigned,
                    new Dictionary<string, string?> { ["userId"] = previous.ToString(System.Globalization.CultureInfo.InvariantCulture) }, task.Id);
                if (assignee is { } next) Record(ActivityAction.TaskAssigned,
                    new Dictionary<string, string?> { ["userId"] = next.ToString(System.Globalization.CultureInfo.InvariantCulture) }, task.Id);
            }
            task.Title = title; task.Description = description; task.Priority = priority; task.AssigneeId = assignee; task.DueDateUtc = due;
            return task;
        }

        public async Task<TaskItem> SetTaskStatusAsync(long taskId, TaskState status, CancellationToken cancellationToken = default)
        {
            var task = await TaskForWriteAsync(taskId, ProjectOperation.WriteTasks, cancellationToken);
            if (!Enum.IsDefined(status)) throw new ArgumentException("Unsupported status.");
            if (workspaceId != null && task.AssigneeId is { } assigned && !await IsEligibleAssigneeAsync(assigned, cancellationToken))
                throw new TaskFlow.Application.Features.Tasks.TaskValidationException("assigneeId", "Choose an active company/project collaborator.");
            if (task.Status != status)
            {
                Record(ActivityAction.TaskStatusChanged, new Dictionary<string, string?> { ["from"] = task.Status.ToString(), ["to"] = status.ToString() }, task.Id);
                task.Status = status;
            }
            return task;
        }

        public async Task DeleteTaskAsync(long taskId, CancellationToken cancellationToken = default)
        {
            var task = await TaskForWriteAsync(taskId, ProjectOperation.DeleteTasks, cancellationToken);
            task.DeletedAtUtc = clock.GetUtcNow().UtcDateTime;
            Record(ActivityAction.TaskDeleted, taskId: task.Id);
        }

        public async Task<TaskItem> CreateTaskAsync(TaskFlow.Application.Features.Tasks.CreateTask request, CancellationToken cancellationToken = default)
        {
            if (operation != ProjectOperation.WriteTasks) throw new InvalidOperationException("Task creation requires the task write operation.");
            if (request.AssigneeId is { } assignee && !await IsEligibleAssigneeAsync(assignee, cancellationToken))
                throw new TaskFlow.Application.Features.Tasks.TaskValidationException("assigneeId", "Choose the project owner or a current member.");
            var task = new TaskItem { ProjectId = project.Id, WorkspaceId = workspaceId, Title = TextRules.Required(request.Title, 200, "title"),
                Description = TextRules.Optional(request.Description, 10_000, "description"), Priority = request.Priority,
                Status = TaskState.ToDo, AssigneeId = request.AssigneeId, DueDateUtc = request.DueDateUtc };
            database.Tasks.Add(task);
            Events.Add(new(ActivityAction.TaskCreated, null, null, task));
            if (task.AssigneeId is { } userId) Events.Add(new(ActivityAction.TaskAssigned,
                JsonSerializer.Serialize(new Dictionary<string, string?> { ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) }), null, task));
            return task;
        }

        public async Task<(ProjectMember Member, string DisplayName)> AddMemberAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (operation != ProjectOperation.ManageMembers) throw new InvalidOperationException("Membership changes require the membership operation.");
            if (userId == project.OwnerId || await database.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == userId && m.RevokedAtUtc == null, cancellationToken))
                throw new MembershipException(MembershipFailure.Conflict);
            if (workspaceId != null && !await ActiveCompanyMemberAsync(userId, cancellationToken))
                throw new MembershipException(MembershipFailure.NotFound);
            var user = await database.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.DisplayName }).SingleOrDefaultAsync(cancellationToken)
                ?? throw new MembershipException(MembershipFailure.NotFound);
            var member = await database.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == userId, cancellationToken);
            if (member is null) { member = new ProjectMember { ProjectId = project.Id, WorkspaceId = workspaceId, UserId = userId }; database.ProjectMembers.Add(member); }
            else { member.RevokedAtUtc = null; member.Role = MembershipRole.Member; }
            Record(ActivityAction.MemberAdded, new Dictionary<string, string?> { ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return (member, user.DisplayName);
        }

        public async Task RemoveMemberAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (operation != ProjectOperation.ManageMembers) throw new InvalidOperationException("Membership changes require the membership operation.");
            if (userId == project.OwnerId) throw new MembershipException(MembershipFailure.Conflict);
            var member = await database.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == userId && m.RevokedAtUtc == null && (workspaceId == null || m.WorkspaceId == workspaceId), cancellationToken)
                ?? throw new MembershipException(MembershipFailure.NotFound);
            var tasks = await database.Tasks.Where(t => t.ProjectId == project.Id && t.AssigneeId == userId && (workspaceId == null || t.WorkspaceId == workspaceId)).OrderBy(t => t.Id).ToListAsync(cancellationToken);
            foreach (var task in tasks)
            {
                task.AssigneeId = null;
                Record(ActivityAction.TaskUnassigned, new Dictionary<string, string?> { ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, task.Id);
            }
            if (workspaceId == null) database.ProjectMembers.Remove(member);
            else member.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
            Record(ActivityAction.MemberRemoved, new Dictionary<string, string?> { ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }

        public void Record(ActivityAction action, IReadOnlyDictionary<string, string?>? details = null, long? taskId = null)
        {
            if (!Enum.IsDefined(action)) throw new ArgumentOutOfRangeException(nameof(action));
            if (taskId is <= 0) throw new ArgumentOutOfRangeException(nameof(taskId));
            if (details?.Keys.Any(string.IsNullOrWhiteSpace) == true)
                throw new ArgumentException("Activity detail keys cannot be blank.", nameof(details));
            var json = details is null ? null : JsonSerializer.Serialize(details);
            if (json?.Length > 10_000) throw new ArgumentException("Activity details exceed 10000 characters.", nameof(details));
            Events.Add(new PendingActivity(action, json, taskId));
        }

        private async Task<bool> ActiveCompanyMemberAsync(long userId, CancellationToken cancellationToken)
        {
            var members = await database.WorkspaceMemberships.FromSqlInterpolated($"SELECT * FROM `WorkspaceMemberships` WHERE `WorkspaceId` = {workspaceId} AND `UserId` = {userId} FOR UPDATE")
                .AsNoTracking().ToListAsync(cancellationToken);
            return members.SingleOrDefault()?.State == WorkspaceMembershipState.Active;
        }
        public async Task<bool> IsEligibleAssigneeAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (userId <= 0 || project.DeletedAtUtc is not null) return false;
            if (workspaceId != null && !await ActiveCompanyMemberAsync(userId, cancellationToken)) return false;
            if (userId == project.OwnerId) return true;
            return await database.ProjectMembers.AsNoTracking().AnyAsync(member => member.ProjectId == project.Id
                && member.UserId == userId && (member.Role == MembershipRole.Member || member.Role == MembershipRole.Editor) && member.RevokedAtUtc == null && (workspaceId == null || member.WorkspaceId == workspaceId), cancellationToken);
        }
    }
}
