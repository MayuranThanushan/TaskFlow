using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Notifications;

public sealed record NotificationBatchResult(int Scanned, int Delivered, long NextTaskId, bool Busy = false);

// In-app delivery is entirely transactional: a database row lock is the work lease.
// A crash rolls back both cursor and inserts. There is no external side effect to retry.
public sealed class NotificationScheduler(TaskFlowDbContext db, TimeProvider clock, IConfiguration configuration)
{
    public async Task<NotificationBatchResult> RunAsync(long workspaceId, CancellationToken token = default)
    {
        if (workspaceId <= 0) throw new ArgumentOutOfRangeException(nameof(workspaceId));
        if (!new[] { "Notifications:Enabled", "Notifications:SchedulingEnabled", "WorkspaceReads:Enabled", "WorkspaceWrites:Enabled", "WorkspaceLifecycle:Enabled" }.All(k => configuration.GetValue<bool>(k)))
            throw new InvalidOperationException("Notification scheduling is disabled.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20)); token = deadline.Token;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        // Same gate as all company resource, membership and preference mutations.
        // SKIP LOCKED avoids concurrent schedulers waiting behind a long mutation.
        var company = await db.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={workspaceId} FOR UPDATE SKIP LOCKED").ToArrayAsync(token);
        if (company.Length == 0) return new(0, 0, 0, true);
        var cursor = await db.NotificationScheduleCursors.SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId, token);
        if (cursor is null) { cursor = new() { WorkspaceId = workspaceId }; db.NotificationScheduleCursors.Add(cursor); }
        // Scan raw IDs, including ineligible rows, so a permanently ineligible prefix
        // cannot starve later tasks. Each invocation performs at most 100 task checks.
        var ids = await db.Tasks.IgnoreQueryFilters().Where(t => t.WorkspaceId == workspaceId && t.Id > cursor.LastTaskId)
            .OrderBy(t => t.Id).Select(t => t.Id).Take(100).ToArrayAsync(token);
        var tasks = await db.Tasks.AsNoTracking().Where(t => ids.Contains(t.Id) && t.WorkspaceId == workspaceId && t.Project.WorkspaceId == workspaceId
            && t.AssigneeId != null && t.DueDateUtc != null && t.Status != TaskState.Done
            && db.WorkspaceMemberships.Any(m => m.WorkspaceId == workspaceId && m.UserId == t.AssigneeId && m.State == WorkspaceMembershipState.Active)
            && db.WorkspaceMemberships.Any(m => m.WorkspaceId == workspaceId && m.UserId == t.Project.OwnerId)
            && (t.Project.OwnerId == t.AssigneeId || db.ProjectMembers.Any(m => m.WorkspaceId == workspaceId && m.ProjectId == t.ProjectId && m.UserId == t.AssigneeId && m.RevokedAtUtc == null)))
            .ToArrayAsync(token);
        var recipients = tasks.Select(t => t.AssigneeId!.Value).Distinct().ToArray();
        var preferences = await db.NotificationPreferences.AsNoTracking().Where(p => p.WorkspaceId == workspaceId && recipients.Contains(p.UserId) && p.InAppEnabled).ToDictionaryAsync(p => p.UserId, token);
        var now = clock.GetUtcNow().UtcDateTime; var delivered = 0;
        foreach (var task in tasks)
        {
            if (!preferences.TryGetValue(task.AssigneeId!.Value, out var preference)) continue;
            var due = task.DueDateUtc!.Value;
            // At the deadline, with a bounded 24-hour outage catch-up. No flood of old reminders.
            if (preference.DueReminderEnabled && due <= now && due >= now.AddDays(-1))
                await Deliver("TaskDue", $"due:{task.Id}:{due.Ticks}");
            if (preference.DailyDigestEnabled)
            {
                var zone = NotificationSchedule.GetZone(preference.TimeZoneId);
                var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(now, zone));
                var at = NotificationSchedule.DigestAtUtc(localDate, preference.DigestHour, zone);
                // A daily inbox digest consists of task cards for overdue / next-24h work.
                // One-hour delivery window; missed days are not backfilled.
                if (now >= at && now < at.AddHours(1) && due <= now.AddDays(1))
                    await Deliver("TaskDigest", $"digest:{task.Id}:{localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
            }
            async Task Deliver(string kind, string key)
            {
                if (await db.UserNotifications.AnyAsync(n => n.WorkspaceId == workspaceId && n.RecipientUserId == task.AssigneeId && n.ScheduleKey == key, token)) return;
                db.UserNotifications.Add(new() { WorkspaceId = workspaceId, RecipientUserId = task.AssigneeId.Value, ProjectId = task.ProjectId,
                    TaskId = task.Id, Kind = kind, ScheduleKey = key, SourceDueDateUtc = due, CreatedAtUtc = now }); delivered++;
            }
        }
        cursor.LastTaskId = ids.Length == 100 ? ids[^1] : 0; cursor.LastBatchAtUtc = now;
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(ids.Length, delivered, cursor.LastTaskId);
    }
}
