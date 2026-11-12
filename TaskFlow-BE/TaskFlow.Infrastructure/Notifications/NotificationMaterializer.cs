using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Notifications;

public static class NotificationMaterializer
{
    public static async Task CaptureAsync(TaskFlowDbContext db, OutboxEvent message, CancellationToken token = default)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Notification materialization requires the source transaction.");
        if (!db.NotificationsEnabled || message.Id != 0 || db.Entry(message).State != EntityState.Added || message.Kind != "Project.TaskAssigned" || message.SchemaVersion != 1 || message.ProjectId is null || message.TaskId is null) return;
        // Source state has been persisted inside this transaction. Bind the intended recipient
        // now; never re-target historical facts to whoever is assigned on a later replay.
        var recipient = await db.Tasks.AsNoTracking().Where(t => t.Id == message.TaskId && t.WorkspaceId == message.WorkspaceId
            && t.ProjectId == message.ProjectId && t.DeletedAtUtc == null && t.Project.DeletedAtUtc == null && t.Project.WorkspaceId == message.WorkspaceId
            && t.AssigneeId != null && t.AssigneeId != message.ActorUserId
            && db.WorkspaceMemberships.Any(m => m.WorkspaceId == message.WorkspaceId && m.UserId == t.AssigneeId && m.State == WorkspaceMembershipState.Active)
            && (t.Project.OwnerId == t.AssigneeId || db.ProjectMembers.Any(m => m.WorkspaceId == message.WorkspaceId && m.ProjectId == t.ProjectId && m.UserId == t.AssigneeId && m.RevokedAtUtc == null)))
            .Select(t => t.AssigneeId).SingleOrDefaultAsync(token);
        if (recipient is not { } user) return;
        if (await db.NotificationPreferences.AnyAsync(p => p.WorkspaceId == message.WorkspaceId && p.UserId == user && (!p.InAppEnabled || !p.TaskAssignedEnabled), token)) return;
        if (db.UserNotifications.Local.Any(n => ReferenceEquals(n.Event, message) && n.RecipientUserId == user)) return;
        db.UserNotifications.Add(new() { WorkspaceId = message.WorkspaceId, RecipientUserId = user, Event = message,
            ProjectId = message.ProjectId.Value, TaskId = message.TaskId.Value, CreatedAtUtc = message.CreatedAtUtc });
    }
}
