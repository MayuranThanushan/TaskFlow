using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Workspaces;

public sealed class WorkspaceLifecycle(IServiceScopeFactory scopes, TaskFlowDbContext reads, IWorkspaceReads admission, TimeProvider clock) : IWorkspaceLifecycle
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static void Deny() => throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
    private static void Conflict() => throw new MembershipException(MembershipFailure.Conflict);
    private static void Missing() => throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private static void Page(int page, int size)
    {
        if (page < 1 || size is < 1 or > 100 || (long)(page - 1) * size > int.MaxValue) throw new ArgumentException("Invalid page.");
    }

    public async Task<WorkspaceMemberPage> MembersAsync(WorkspaceReadContext context, int page, int size, CancellationToken token = default)
    {
        Page(page, size);
        await using var snapshot = await reads.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await admission.AuthorizeAsync(context, token);
        var actor = await reads.WorkspaceMemberships.AsNoTracking().SingleAsync(m => m.WorkspaceId == context.WorkspaceId && m.UserId == context.ActorId, token);
        if (actor.Role == WorkspaceRole.Guest) Deny();
        var manage = actor.Role is WorkspaceRole.Owner or WorkspaceRole.Admin;
        var query = from m in reads.WorkspaceMemberships.AsNoTracking()
                    join user in reads.Users.AsNoTracking() on m.UserId equals user.Id
                    where m.WorkspaceId == context.WorkspaceId && (manage || m.State == WorkspaceMembershipState.Active)
                    orderby m.UserId
                    select new { m.UserId, user.DisplayName, m.Role, m.State };
        var total = await query.CountAsync(token);
        var rows = await query.Skip((page - 1) * size).Take(size).ToArrayAsync(token);
        await snapshot.CommitAsync(token);
        return new(rows.Select(r => new WorkspaceMemberDto(Id(r.UserId), r.DisplayName, manage ? r.Role.ToString() : null, manage ? r.State.ToString() : null)).ToArray(), page, size, total, (int)Math.Ceiling(total / (double)size));
    }

    public async Task<WorkspaceAuditPage> AuditAsync(WorkspaceReadContext context, int page, int size, CancellationToken token = default)
    {
        Page(page, size);
        await using var snapshot = await reads.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await admission.AuthorizeAsync(context, token);
        if (!await reads.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == context.WorkspaceId && m.UserId == context.ActorId && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Admin), token)) Deny();
        var query = reads.WorkspaceAuditLogs.AsNoTracking().Where(a => a.WorkspaceId == context.WorkspaceId);
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(token);
        await snapshot.CommitAsync(token);
        return new(rows.Select(a => new WorkspaceAuditDto(Id(a.Id), Id(a.ActorUserId), Id(a.TargetUserId), a.ProjectId is { } p ? Id(p) : null, a.Action, a.FromRole, a.ToRole, a.CreatedAtUtc)).ToArray(), page, size, total, (int)Math.Ceiling(total / (double)size));
    }

    // Every lifecycle writer uses the same workspace -> membership -> project ordering as scoped CRUD.
    private async Task Write(WorkspaceWriteContext context, Func<TaskFlowDbContext, WorkspaceMembership, Task> change, CancellationToken token)
    {
        if (context.ActorId <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var gate = await db.Workspaces.FromSqlInterpolated($"SELECT * FROM `Workspaces` WHERE `Id` = {context.WorkspaceId} FOR UPDATE").ToListAsync(token);
        if (gate.Count != 1) Missing();
        var actor = await Member(db, context, context.ActorId, token);
        if (actor.State != WorkspaceMembershipState.Active) Missing();
        await change(db, actor);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
    private static async Task<WorkspaceMembership> Member(TaskFlowDbContext db, WorkspaceWriteContext context, long user, CancellationToken token)
    {
        var members = await db.WorkspaceMemberships.FromSqlInterpolated($"SELECT * FROM `WorkspaceMemberships` WHERE `WorkspaceId` = {context.WorkspaceId} AND `UserId` = {user} FOR UPDATE").ToListAsync(token);
        return members.SingleOrDefault() ?? throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    }
    private static void Manage(WorkspaceMembership actor, WorkspaceMembership target, WorkspaceRole? desired = null)
    {
        if (actor.Role == WorkspaceRole.Owner) return;
        if (actor.Role != WorkspaceRole.Admin || target.Role is WorkspaceRole.Admin or WorkspaceRole.Owner || desired is WorkspaceRole.Admin or WorkspaceRole.Owner) Deny();
    }
    private static async Task ProtectOwner(TaskFlowDbContext db, WorkspaceMembership target, CancellationToken token)
    {
        if (target.Role == WorkspaceRole.Owner && target.State == WorkspaceMembershipState.Active
            && await db.WorkspaceMemberships.CountAsync(m => m.WorkspaceId == target.WorkspaceId && m.State == WorkspaceMembershipState.Active && m.Role == WorkspaceRole.Owner, token) <= 1) Conflict();
    }
    private static async Task NoOwnedProjects(TaskFlowDbContext db, WorkspaceMembership target, CancellationToken token)
    {
        if (await db.Projects.AnyAsync(p => p.WorkspaceId == target.WorkspaceId && p.OwnerId == target.UserId, token)) Conflict();
    }
    private void Audit(TaskFlowDbContext db, WorkspaceWriteContext context, long user, string action, string? from = null, string? to = null, long? project = null) =>
        db.WorkspaceAuditLogs.Add(new WorkspaceAuditLog { WorkspaceId = context.WorkspaceId, ActorUserId = context.ActorId, TargetUserId = user, ProjectId = project, Action = action, FromRole = from, ToRole = to });

    private async Task Password(long actor, string? password, CancellationToken token)
    {
        if (string.IsNullOrEmpty(password) || password.Length > 128) Deny();
        // Separate identity context: failed-password lockout counters must commit even when governance rolls back.
        await using var scope = scopes.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var user = await manager.FindByIdAsync(Id(actor));
        token.ThrowIfCancellationRequested();
        if (user is null || user.TwoFactorEnabled || !(await signIn.CheckPasswordSignInAsync(user, password!, lockoutOnFailure: true)).Succeeded) Deny();
    }

    public Task SetRoleAsync(WorkspaceWriteContext context, long userId, WorkspaceRole role, string? password, CancellationToken token = default) => Write(context, async (db, actor) =>
    {
        if (!Enum.IsDefined(role)) throw new ArgumentException("Unsupported role.");
        var target = await Member(db, context, userId, token); Manage(actor, target, role);
        if (target.State != WorkspaceMembershipState.Active) Conflict();
        if (target.Role == role) return;
        if (target.UserId == actor.UserId && target.Role != WorkspaceRole.Owner) Deny();
        if (role == WorkspaceRole.Owner || target.Role == WorkspaceRole.Owner) await Password(context.ActorId, password, token);
        if (target.Role == WorkspaceRole.Owner) await ProtectOwner(db, target, token);
        if (role == WorkspaceRole.Guest) await NoOwnedProjects(db, target, token);
        var before = target.Role.ToString(); target.Role = role;
        Audit(db, context, userId, "CompanyRoleChanged", before, role.ToString());
    }, token);

    public Task SetStateAsync(WorkspaceWriteContext context, long userId, WorkspaceMembershipState state, CancellationToken token = default) => Write(context, async (db, actor) =>
    {
        if (!Enum.IsDefined(state)) throw new ArgumentException("Unsupported state.");
        var target = await Member(db, context, userId, token); Manage(actor, target);
        // Owner suspension/removal is deliberately unavailable through this generic endpoint.
        // Transfer/demote with password verification first; this also protects the final active owner.
        if (target.Role == WorkspaceRole.Owner || target.UserId == actor.UserId) Conflict();
        if (target.State == state) return;
        if (target.State == WorkspaceMembershipState.Removed) Conflict();
        if (state == WorkspaceMembershipState.Removed) await NoOwnedProjects(db, target, token);
        var before = target.State.ToString(); target.State = state;
        if (state != WorkspaceMembershipState.Active) await Cleanup(db, context, userId, state == WorkspaceMembershipState.Removed, token);
        Audit(db, context, userId, state == WorkspaceMembershipState.Active ? "CompanyMemberReactivated" : state == WorkspaceMembershipState.Suspended ? "CompanyMemberSuspended" : "CompanyMemberRemoved", before, state.ToString());
    }, token);

    public Task LeaveAsync(WorkspaceWriteContext context, string? password, CancellationToken token = default) => Write(context, async (db, actor) =>
    {
        if (actor.Role == WorkspaceRole.Owner) await Password(context.ActorId, password, token);
        await ProtectOwner(db, actor, token); await NoOwnedProjects(db, actor, token);
        actor.State = WorkspaceMembershipState.Removed;
        await Cleanup(db, context, context.ActorId, true, token);
        Audit(db, context, context.ActorId, "CompanyMemberLeft");
    }, token);

    public Task TransferCompanyAsync(WorkspaceWriteContext context, long userId, string password, CancellationToken token = default) => Write(context, async (db, actor) =>
    {
        if (actor.Role != WorkspaceRole.Owner) Deny();
        var target = await Member(db, context, userId, token);
        if (target.UserId == actor.UserId || target.State != WorkspaceMembershipState.Active || target.Role is WorkspaceRole.Guest or WorkspaceRole.Owner) Conflict();
        await Password(context.ActorId, password, token);
        var before = target.Role.ToString(); target.Role = WorkspaceRole.Owner; actor.Role = WorkspaceRole.Admin;
        Audit(db, context, userId, "CompanyOwnershipTransferred", before, "Owner");
        Audit(db, context, actor.UserId, "CompanyRoleChanged", "Owner", "Admin");
    }, token);

    private async Task Cleanup(TaskFlowDbContext db, WorkspaceWriteContext context, long user, bool revoke, CancellationToken token, long? onlyProject = null)
    {
        var projects = await db.Projects.FromSqlInterpolated($"SELECT * FROM `Projects` WHERE `WorkspaceId` = {context.WorkspaceId} ORDER BY `Id` FOR UPDATE").IgnoreQueryFilters().ToListAsync(token);
        var ids = projects.Where(p => onlyProject == null || p.Id == onlyProject).Select(p => p.Id).ToArray();
        var tasks = await db.Tasks.Where(t => t.WorkspaceId == context.WorkspaceId && ids.Contains(t.ProjectId) && t.AssigneeId == user).OrderBy(t => t.ProjectId).ThenBy(t => t.Id).ToListAsync(token);
        foreach (var task in tasks)
        {
            task.AssigneeId = null;
            db.ActivityLogs.Add(new ActivityLog { WorkspaceId = context.WorkspaceId, ProjectId = task.ProjectId, TaskId = task.Id, ActorUserId = context.ActorId, ActionType = ActivityAction.TaskUnassigned, Details = JsonSerializer.Serialize(new { userId = Id(user) }) });
        }
        if (revoke)
        {
            var grants = await db.ProjectMembers.IgnoreQueryFilters().Where(m => m.WorkspaceId == context.WorkspaceId && ids.Contains(m.ProjectId) && m.UserId == user && m.RevokedAtUtc == null).ToArrayAsync(token);
            foreach (var grant in grants)
            {
                grant.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
                db.ActivityLogs.Add(new ActivityLog { WorkspaceId = context.WorkspaceId, ProjectId = grant.ProjectId, ActorUserId = context.ActorId, ActionType = ActivityAction.MemberRemoved, Details = JsonSerializer.Serialize(new { userId = Id(user) }) });
            }
        }
    }
    private static async Task<Project> Project(TaskFlowDbContext db, WorkspaceWriteContext context, long id, CancellationToken token)
    {
        var rows = await db.Projects.FromSqlInterpolated($"SELECT * FROM `Projects` WHERE `WorkspaceId` = {context.WorkspaceId} AND `Id` = {id} FOR UPDATE").IgnoreQueryFilters().ToListAsync(token);
        var project = rows.SingleOrDefault();
        if (project is null || project.DeletedAtUtc != null) Missing();
        if (project!.OwnerId != context.ActorId)
        {
            if (await db.ProjectMembers.AnyAsync(m => m.WorkspaceId == context.WorkspaceId && m.ProjectId == id && m.UserId == context.ActorId && m.RevokedAtUtc == null, token)) Deny();
            Missing();
        }
        return project;
    }
    public Task SetProjectRoleAsync(WorkspaceWriteContext context, long projectId, long userId, MembershipRole role, CancellationToken token = default) => Write(context, async (db, actor) =>
    {
        if (role is not (MembershipRole.Editor or MembershipRole.Viewer)) throw new ArgumentException("Unsupported project role.");
        await Project(db, context, projectId, token);
        var target = await Member(db, context, userId, token);
        if (target.State != WorkspaceMembershipState.Active) Missing();
        var grant = await db.ProjectMembers.SingleOrDefaultAsync(m => m.WorkspaceId == context.WorkspaceId && m.ProjectId == projectId && m.UserId == userId && m.RevokedAtUtc == null, token);
        if (grant is null) Missing();
        if (grant!.Role == role) return;
        var before = grant.Role.ToString(); grant.Role = role;
        if (role == MembershipRole.Viewer) await Cleanup(db, context, userId, false, token, projectId);
        db.ActivityLogs.Add(new ActivityLog { WorkspaceId = context.WorkspaceId, ProjectId = projectId, ActorUserId = context.ActorId, ActionType = ActivityAction.MemberRoleChanged, Details = JsonSerializer.Serialize(new { userId = Id(userId), from = before, to = role.ToString() }) });
        Audit(db, context, userId, "ProjectRoleChanged", before, role.ToString(), projectId);
    }, token);

    public Task TransferProjectAsync(WorkspaceWriteContext context, long projectId, long userId, string password, CancellationToken token = default) => Write(context, async (db, actor) =>
    {
        var project = await Project(db, context, projectId, token);
        var target = await Member(db, context, userId, token);
        if (target.State != WorkspaceMembershipState.Active || target.Role == WorkspaceRole.Guest || userId == context.ActorId) Conflict();
        await Password(context.ActorId, password, token);
        var oldGrant = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == context.ActorId, token);
        if (oldGrant is null) db.ProjectMembers.Add(new ProjectMember { WorkspaceId = context.WorkspaceId, ProjectId = projectId, UserId = context.ActorId, Role = MembershipRole.Editor });
        else { oldGrant.Role = MembershipRole.Editor; oldGrant.RevokedAtUtc = null; }
        var newGrant = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == projectId && m.UserId == userId, token);
        var recipientBefore = newGrant is { RevokedAtUtc: null } ? newGrant.Role.ToString() : "None";
        if (newGrant != null) newGrant.RevokedAtUtc = clock.GetUtcNow().UtcDateTime;
        project.OwnerId = userId;
        db.ActivityLogs.Add(new ActivityLog { WorkspaceId = context.WorkspaceId, ProjectId = projectId, ActorUserId = context.ActorId, ActionType = ActivityAction.ProjectOwnershipTransferred, Details = JsonSerializer.Serialize(new { fromUserId = Id(context.ActorId), toUserId = Id(userId) }) });
        Audit(db, context, userId, "ProjectOwnershipTransferred", recipientBefore, "Owner", projectId);
        Audit(db, context, context.ActorId, "ProjectRoleChanged", "Owner", "Editor", projectId);
    }, token);
}
