using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Projects;

public sealed class ProjectTransactions(IServiceScopeFactory scopeFactory) : IProjectTransactions
{
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
        Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken cancellationToken)
    {
        if (actorId is null or <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        ArgumentNullException.ThrowIfNull(mutation);
        cancellationToken.ThrowIfCancellationRequested();
        // A fresh scope keeps old tracked entities, snapshots and failed writes out of this boundary.
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        Project project;
        if (projectId is not null)
        {
            // Materialize directly: composing Single/Where over FOR UPDATE can change MySQL locking semantics.
            var locked = await database.Projects.FromSqlInterpolated(
                $"SELECT * FROM `Projects` WHERE `Id` = {projectId.Value} FOR UPDATE")
                .IgnoreQueryFilters().ToListAsync(cancellationToken);
            project = locked.SingleOrDefault() ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
            var role = await ProjectAccess.RoleAsync(database, actorId.Value, project.Id, cancellationToken);
            var decision = ProjectAccessPolicy.Decide(actorId, role, operation);
            if (decision != ProjectAccessDecision.Allowed) throw new ProjectAccessException(decision);
        }
        else
        {
            project = new Project { OwnerId = actorId.Value };
            database.Projects.Add(project);
        }

        var originalId = project.Id;
        var ownerId = project.OwnerId;
        var boundary = new Mutation(database, project);
        if (projectId is null) boundary.Record(ActivityAction.ProjectCreated);
        var result = await mutation(boundary, cancellationToken);
        if (project.Id != originalId || project.OwnerId != ownerId)
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
                if (activity.TaskId is { } taskId && !await database.Tasks.IgnoreQueryFilters()
                    .AnyAsync(task => task.Id == taskId && task.ProjectId == project.Id, cancellationToken))
                    throw new InvalidOperationException("Activity task must belong to the locked project.");
                database.ActivityLogs.Add(new ActivityLog
                {
                    ProjectId = project.Id, ActorUserId = actorId.Value, TaskId = activity.TaskId,
                    ActionType = activity.Action, Details = activity.Details
                });
            }
            await database.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private sealed record PendingActivity(ActivityAction Action, string? Details, long? TaskId);

    private sealed class Mutation(TaskFlowDbContext database, Project project) : IProjectMutation
    {
        public Project Project => project;
        public List<PendingActivity> Events { get; } = [];

        public async Task<(ProjectMember Member, string DisplayName)> AddMemberAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (userId == project.OwnerId || await database.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == userId, cancellationToken))
                throw new MembershipException(MembershipFailure.Conflict);
            var user = await database.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.DisplayName }).SingleOrDefaultAsync(cancellationToken)
                ?? throw new MembershipException(MembershipFailure.NotFound);
            var member = new ProjectMember { ProjectId = project.Id, UserId = userId };
            database.ProjectMembers.Add(member);
            Record(ActivityAction.MemberAdded, new Dictionary<string, string?> { ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return (member, user.DisplayName);
        }

        public async Task RemoveMemberAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (userId == project.OwnerId) throw new MembershipException(MembershipFailure.Conflict);
            var member = await database.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == userId, cancellationToken)
                ?? throw new MembershipException(MembershipFailure.NotFound);
            var tasks = await database.Tasks.Where(t => t.ProjectId == project.Id && t.AssigneeId == userId).OrderBy(t => t.Id).ToListAsync(cancellationToken);
            foreach (var task in tasks)
            {
                task.AssigneeId = null;
                Record(ActivityAction.TaskUnassigned, new Dictionary<string, string?> { ["userId"] = userId.ToString(System.Globalization.CultureInfo.InvariantCulture) }, task.Id);
            }
            database.ProjectMembers.Remove(member);
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

        public Task<bool> IsEligibleAssigneeAsync(long userId, CancellationToken cancellationToken = default)
        {
            if (userId <= 0 || project.DeletedAtUtc is not null) return Task.FromResult(false);
            if (userId == project.OwnerId) return Task.FromResult(true);
            return database.ProjectMembers.AsNoTracking().AnyAsync(member => member.ProjectId == project.Id
                && member.UserId == userId && member.Role == MembershipRole.Member, cancellationToken);
        }
    }
}
