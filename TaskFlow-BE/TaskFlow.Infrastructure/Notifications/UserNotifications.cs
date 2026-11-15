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
        && (n.CommentId == null || db.TaskComments.Any(comment => comment.WorkspaceId == c.WorkspaceId && comment.Id == n.CommentId && comment.TaskId == n.TaskId && comment.DeletedAtUtc == null))
        && db.Tasks.Any(t => t.Id == n.TaskId && t.ProjectId == n.ProjectId && t.WorkspaceId == c.WorkspaceId && t.DeletedAtUtc == null && (n.ScheduleKey == null || (t.AssigneeId == c.ActorId && t.Status != TaskState.Done && t.DueDateUtc == n.SourceDueDateUtc))));

    public async Task<NotificationPage> ListAsync(WorkspaceReadContext c, int page, int pageSize, bool unreadOnly, CancellationToken token = default, long? taskId = null)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Authorize(c, token);
        if (page < 1 || pageSize is < 1 or > 100 || (long)(page - 1) * pageSize > 10000) throw new ArgumentException("Invalid notification page.");
        var visible = Visible(c).AsNoTracking();
        var unread = await visible.CountAsync(n => n.ReadAtUtc == null, token);
        var query = unreadOnly ? visible.Where(n => n.ReadAtUtc == null) : visible;
        if (taskId is {} selected) query = query.Where(n => n.TaskId == selected);
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
    public async Task<NotificationGroupPage> GroupsAsync(WorkspaceReadContext c,int page,int pageSize,bool unreadOnly,CancellationToken token=default)
    {
        await using var snapshot=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,token);await Authorize(c,token);
        if(page<1||pageSize is <1 or >100||(long)(page-1)*pageSize>10000)throw new ArgumentException("Invalid group page.");
        var visible=Visible(c).AsNoTracking();var unread=await visible.CountAsync(n=>n.ReadAtUtc==null,token);
        var groups=visible.GroupBy(n=>new{n.TaskId,n.ProjectId}).Select(g=>new{g.Key.TaskId,g.Key.ProjectId,Last=g.Max(n=>n.Id),Count=g.Count(),Unread=g.Count(n=>n.ReadAtUtc==null)});
        if(unreadOnly)groups=groups.Where(g=>g.Unread>0);
        var total=await groups.CountAsync(token);
        var rows=await (from g in groups join t in db.Tasks.AsNoTracking() on g.TaskId equals t.Id join p in db.Projects.AsNoTracking() on g.ProjectId equals p.Id
            join latest in db.UserNotifications.AsNoTracking() on g.Last equals latest.Id orderby g.Last descending
            select new{g.TaskId,g.ProjectId,Title=t.Title,Name=p.Name,g.Last,latest.CreatedAtUtc,g.Count,g.Unread}).Skip((page-1)*pageSize).Take(pageSize).ToArrayAsync(token);
        await snapshot.CommitAsync(token);
        static string Id(long value)=>value.ToString(CultureInfo.InvariantCulture);
        return new(rows.Select(g=>new NotificationGroupDto(Id(g.TaskId),Id(g.ProjectId),g.Title,g.Name,Id(g.Last),g.CreatedAtUtc,g.Count,g.Unread)).ToArray(),page,pageSize,total,(int)Math.Ceiling(total/(double)pageSize),unread);
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
        await Gate(c, token);
        var row = await Visible(c).SingleOrDefaultAsync(n => n.Id == notificationId, token) ?? throw Missing();
        if (isRead && row.ReadAtUtc == null) row.ReadAtUtc = clock.GetUtcNow().UtcDateTime;
        else if (!isRead) row.ReadAtUtc = null;
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }
    private async Task Gate(WorkspaceReadContext c, CancellationToken token)
    {
        var companies = await db.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={c.WorkspaceId} FOR UPDATE").ToArrayAsync(token);
        if (companies.Length != 1) throw Missing();
        await Authorize(c, token);
    }
    public async Task MarkReadAsync(WorkspaceReadContext c, IReadOnlyList<long> ids, CancellationToken token = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await Gate(c, token);
        if (ids.Count is < 1 or > 100 || ids.Any(id => id <= 0) || ids.Distinct().Count() != ids.Count) throw new ArgumentException("Choose 1 to 100 distinct notifications.");
        var rows = await Visible(c).Where(n => ids.Contains(n.Id)).ToArrayAsync(token);
        // Validate the whole selection first: no partial update can reveal inaccessible IDs.
        if (rows.Length != ids.Count) throw Missing();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var row in rows.Where(n => n.ReadAtUtc == null)) row.ReadAtUtc = now;
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
    }
    private static NotificationPreferencesDto Map(NotificationPreference? value) => new(value?.InAppEnabled ?? true, value?.TaskAssignedEnabled ?? true, false, (value?.Version ?? 0).ToString(CultureInfo.InvariantCulture), value?.DueReminderEnabled ?? false, value?.DailyDigestEnabled ?? false, value?.TimeZoneId ?? "UTC", value?.DigestHour ?? 9, value?.WatcherDeliveryMode ?? "Immediate", value?.QuietStartHour, value?.QuietEndHour);
    public async Task<NotificationPreferencesDto> PreferencesAsync(WorkspaceReadContext c, CancellationToken token = default)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await Authorize(c, token);
        var value = await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.WorkspaceId == c.WorkspaceId && p.UserId == c.ActorId, token);
        await snapshot.CommitAsync(token); return Map(value);
    }
    public async Task<NotificationPreferencesDto> SetPreferencesAsync(WorkspaceReadContext c, bool inAppEnabled, bool taskAssignedEnabled, long expectedVersion, CancellationToken token = default, NotificationScheduleOptions? schedule = null, NotificationNoiseOptions? noise = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        await Gate(c, token);
        var value = await db.NotificationPreferences.SingleOrDefaultAsync(p => p.WorkspaceId == c.WorkspaceId && p.UserId == c.ActorId, token);
        if (expectedVersion != (value?.Version ?? 0)) throw new ResourceVersionException(409, "resource.conflict");
        if (value is null && inAppEnabled && taskAssignedEnabled && schedule is null && noise is null) { await transaction.CommitAsync(token); return Map(null); }
        if (value is null) { value = new() { WorkspaceId = c.WorkspaceId, UserId = c.ActorId }; db.NotificationPreferences.Add(value); }
        if (schedule is not null)
        {
            _ = NotificationSchedule.GetZone(schedule.TimeZoneId);
            if (schedule.DigestHour is < 0 or > 23) throw new ArgumentException("Invalid digest hour.");
            value.DueReminderEnabled = schedule.DueReminderEnabled; value.DailyDigestEnabled = schedule.DailyDigestEnabled;
            value.TimeZoneId = schedule.TimeZoneId; value.DigestHour = schedule.DigestHour;
        }
        if (noise is not null)
        {
            if (noise.WatcherDeliveryMode is not ("Immediate" or "Digest" or "Off") || noise.QuietStartHour.HasValue != noise.QuietEndHour.HasValue
                || noise.QuietStartHour is < 0 or > 23 || noise.QuietEndHour is < 0 or > 23 || noise.QuietStartHour is not null && noise.QuietStartHour == noise.QuietEndHour)
                throw new TaskFlow.Application.Features.Tasks.TaskValidationException("watcherDeliveryMode", "Choose Immediate, Digest or Off, and distinct quiet start/end hours or neither.");
            value.WatcherDeliveryMode = noise.WatcherDeliveryMode; value.QuietStartHour = noise.QuietStartHour; value.QuietEndHour = noise.QuietEndHour;
        }
        if (value.WatcherDeliveryMode == "Digest" && !value.DailyDigestEnabled) throw new TaskFlow.Application.Features.Tasks.TaskValidationException("dailyDigestEnabled", "Enable daily digest before choosing Digest watcher delivery.");
        value.InAppEnabled = inAppEnabled; value.TaskAssignedEnabled = taskAssignedEnabled;
        await db.SaveChangesAsync(token);
        if (!inAppEnabled || value.WatcherDeliveryMode == "Off")
            await db.WatcherNotificationIntents.Where(i => i.WorkspaceId == c.WorkspaceId && i.RecipientUserId == c.ActorId && i.State == "Pending")
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.State, "Suppressed").SetProperty(i => i.Outcome, "Muted").SetProperty(i => i.CompletedAtUtc, clock.GetUtcNow().UtcDateTime), token);
        else if (value.Version != expectedVersion)
        {
            var ready = WatcherTiming.ReadyAt(clock.GetUtcNow().UtcDateTime, value);
            await db.WatcherNotificationIntents.Where(i => i.WorkspaceId == c.WorkspaceId && i.RecipientUserId == c.ActorId && i.State == "Pending")
                .ExecuteUpdateAsync(set => set.SetProperty(i => i.AvailableAtUtc, ready), token);
        }
        await transaction.CommitAsync(token); return Map(value);
    }
}
