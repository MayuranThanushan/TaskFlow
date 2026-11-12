using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Notifications;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Notifications;

public sealed class UserNotifications(TaskFlowDbContext db, TimeProvider clock) : IUserNotifications
{
    private static ProjectAccessException Missing() => new(ProjectAccessDecision.NotFound);
    private async Task Authorize(WorkspaceReadContext c, CancellationToken token)
    {
        if (c.ActorId <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        if (!await db.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == c.WorkspaceId && m.UserId == c.ActorId && m.State == WorkspaceMembershipState.Active, token)) throw Missing();
    }
    // A single predicate governs lists, counts and writes. Never trust a stored notification
    // as continuing authorization, and never render cached names from revoked resources.
    private IQueryable<UserNotification> Visible(WorkspaceReadContext c) => db.UserNotifications.Where(n => n.WorkspaceId == c.WorkspaceId && n.RecipientUserId == c.ActorId
        && db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == c.ActorId && m.State == WorkspaceMembershipState.Active)
        && db.Projects.Any(p => p.Id == n.ProjectId && p.WorkspaceId == c.WorkspaceId && p.DeletedAtUtc == null
            && db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == p.OwnerId)
            && (p.OwnerId == c.ActorId || db.ProjectMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.ProjectId == p.Id && m.UserId == c.ActorId && m.RevokedAtUtc == null)))
        && db.Tasks.Any(t => t.Id == n.TaskId && t.ProjectId == n.ProjectId && t.WorkspaceId == c.WorkspaceId && t.DeletedAtUtc == null));

    public async Task<NotificationPage> ListAsync(WorkspaceReadContext c, int page, int pageSize, bool unreadOnly, CancellationToken token = default)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Authorize(c, token);
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > 10000) throw new ArgumentException("Invalid notification page.");
        var visible = Visible(c).AsNoTracking();
        var unread = await visible.CountAsync(n => n.ReadAtUtc == null, token);
        var query = unreadOnly ? visible.Where(n => n.ReadAtUtc == null) : visible;
        var total = await query.CountAsync(token);
        var rows = await (from n in query
                          join p in db.Projects.AsNoTracking() on n.ProjectId equals p.Id
                          join t in db.Tasks.AsNoTracking() on n.TaskId equals t.Id
                          orderby n.Id descending
                          select new { n.Id, n.WorkspaceId, n.Kind, n.ProjectId, ProjectName = p.Name, n.TaskId, TaskTitle = t.Title, n.CreatedAtUtc, n.ReadAtUtc })
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        await snapshot.CommitAsync(token);
        static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
        return new(rows.Select(n => new NotificationDto(Id(n.Id), Id(n.WorkspaceId), n.Kind, Id(n.ProjectId), n.ProjectName, Id(n.TaskId), n.TaskTitle, n.CreatedAtUtc, n.ReadAtUtc)).ToArray(), page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize), unread);
    }
    public async Task<NotificationCountDto> CountAsync(WorkspaceReadContext c, CancellationToken token = default)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Authorize(c, token); var count = await Visible(c).CountAsync(n => n.ReadAtUtc == null, token);
        await snapshot.CommitAsync(token); return new(count);
    }
    public async Task SetReadAsync(WorkspaceReadContext c, long notificationId, bool isRead, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        // Same company gate as resource/membership writers; authority cannot change mid-write.
        var companies = await db.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={c.WorkspaceId} FOR UPDATE").ToArrayAsync(token);
        if (companies.Length != 1) throw Missing();
        await Authorize(c, token);
        var row = await Visible(c).SingleOrDefaultAsync(n => n.Id == notificationId, token) ?? throw Missing();
        if (isRead && row.ReadAtUtc == null) row.ReadAtUtc = clock.GetUtcNow().UtcDateTime;
        else if (!isRead) row.ReadAtUtc = null;
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }
}
