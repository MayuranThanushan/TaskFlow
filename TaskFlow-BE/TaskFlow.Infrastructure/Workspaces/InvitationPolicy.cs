using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Workspaces;
public static class InvitationPolicy
{
    public static async Task<DateTime> Now(TaskFlowDbContext db, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction(); command.CommandText = "SELECT UTC_TIMESTAMP(6)";
        return DateTime.SpecifyKind(Convert.ToDateTime(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
    public static IQueryable<WorkspaceInvitation> Valid(TaskFlowDbContext db, long workspace, DateTime now) => db.WorkspaceInvitations.Where(i => i.WorkspaceId == workspace && i.State == "Pending" && i.ExpiresAtUtc > now
        && db.WorkspaceMemberships.Any(m => m.WorkspaceId == workspace && m.UserId == i.InviterUserId && m.State == WorkspaceMembershipState.Active && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Admin))
        && (i.ProjectId == null || db.Projects.IgnoreQueryFilters().Any(p => p.Id == i.ProjectId && p.WorkspaceId == workspace && p.DeletedAtUtc == null && p.OwnerId == i.InviterUserId)));
    public static async Task<int> Reservations(TaskFlowDbContext db, long workspace, DateTime now, CancellationToken token) => await Valid(db, workspace, now).CountAsync(i => !db.WorkspaceMemberships.Any(m => m.WorkspaceId == workspace && m.State == WorkspaceMembershipState.Active && db.Users.Any(u => u.Id == m.UserId && u.NormalizedEmail == i.NormalizedEmail)), token);
    public static void Finish(WorkspaceInvitation i, string state)
    { i.State = state; i.PendingEmail = null; i.TokenHash = null; i.ProtectedMessage = null; i.LeaseToken = null; i.LeaseExpiresAtUtc = null; i.DeliveryState = "Cancelled"; i.Version++; }
    public static void Audit(TaskFlowDbContext db, WorkspaceInvitation i, long actor, string action, DateTime now) => db.InvitationTransitions.Add(new() { WorkspaceId = i.WorkspaceId, InvitationId = i.Id, ActorUserId = actor, Action = action, Version = i.Version, CreatedAtUtc = now, CorrelationId = AuditCorrelation.Current() });
    public static async Task Sweep(TaskFlowDbContext db, long workspace, DateTime now, CancellationToken token, long actor)
    {
        var valid = await Valid(db, workspace, now).Select(i => i.Id).ToArrayAsync(token);
        foreach (var i in await db.WorkspaceInvitations.Where(i => i.WorkspaceId == workspace && i.State == "Pending" && !valid.Contains(i.Id)).ToListAsync(token))
        { Finish(i, i.ExpiresAtUtc <= now ? "Expired" : "Revoked"); Audit(db, i, actor, i.State, now); }
        await db.SaveChangesAsync(token);
    }
}
