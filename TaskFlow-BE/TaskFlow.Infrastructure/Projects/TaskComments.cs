using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Comments;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Notifications;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Projects;
public sealed class TaskComments(TaskFlowDbContext db, TimeProvider clock) : ITaskComments
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static ProjectAccessException Missing() => new(ProjectAccessDecision.NotFound);
    private async Task<(TaskItem Task, long Owner, bool Write)> Require(WorkspaceReadContext c, long taskId, CancellationToken token)
    {
        if (c.ActorId <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        var task = await db.Tasks.AsNoTracking().Where(t => t.Id == taskId && t.WorkspaceId == c.WorkspaceId && t.Project.WorkspaceId == c.WorkspaceId
            && t.DeletedAtUtc == null && t.Project.DeletedAtUtc == null
            && db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == c.ActorId && m.State == WorkspaceMembershipState.Active)
            && db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == t.Project.OwnerId)
            && (t.Project.OwnerId == c.ActorId || db.ProjectMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.ProjectId == t.ProjectId && m.UserId == c.ActorId && m.RevokedAtUtc == null))
            && (t.AssigneeId == null || db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == t.AssigneeId)
                && (t.Project.OwnerId == t.AssigneeId || db.ProjectMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.ProjectId == t.ProjectId && m.UserId == t.AssigneeId && m.RevokedAtUtc == null && m.Role != MembershipRole.Viewer))))
            .Select(t => new { Task = t, Owner = t.Project.OwnerId }).SingleOrDefaultAsync(token) ?? throw Missing();
        var role = await db.WorkspaceMemberships.Where(m => m.WorkspaceId == c.WorkspaceId && m.UserId == c.ActorId).Select(m => m.Role).SingleAsync(token);
        var write = role != WorkspaceRole.Guest && (task.Owner == c.ActorId || await db.ProjectMembers.AnyAsync(m => m.WorkspaceId == c.WorkspaceId && m.ProjectId == task.Task.ProjectId && m.UserId == c.ActorId && m.RevokedAtUtc == null && m.Role != MembershipRole.Viewer, token));
        return (task.Task, task.Owner, write);
    }
    private IQueryable<TaskComment> Rows(WorkspaceReadContext c, long task) => db.TaskComments.Where(row => row.WorkspaceId == c.WorkspaceId && row.TaskId == task);
    private IQueryable<TaskFlow.Infrastructure.Identity.ApplicationUser> People(long workspace, long project, long owner) => db.Users.AsNoTracking().Where(u =>
        db.WorkspaceMemberships.Any(m => m.WorkspaceId == workspace && m.UserId == u.Id && m.State == WorkspaceMembershipState.Active)
        && (u.Id == owner || db.ProjectMembers.Any(m => m.WorkspaceId == workspace && m.ProjectId == project && m.UserId == u.Id && m.RevokedAtUtc == null)));
    private async Task<TaskCommentDto> Map(WorkspaceReadContext c, TaskComment row, long owner, bool write, CancellationToken token)
    {
        var ids = row.DeletedAtUtc == null ? CommentMentions.Parse(row.Body) : [];
        var mentions = await People(c.WorkspaceId, row.ProjectId, owner).Where(u => ids.Contains(u.Id)).OrderBy(u => u.Id).Select(u => new CommentMentionDto(Id(u.Id), u.DisplayName)).ToArrayAsync(token);
        var name = await db.Users.Where(u => u.Id == row.AuthorId).Select(u => u.DisplayName).SingleAsync(token);
        return new(Id(row.Id), Id(row.TaskId), Id(row.AuthorId), name, row.DeletedAtUtc == null ? row.Body : null, Id(row.Version), row.CreatedAtUtc, row.EditedAtUtc, row.DeletedAtUtc,
            write && row.DeletedAtUtc == null && row.AuthorId == c.ActorId, write && row.DeletedAtUtc == null && (row.AuthorId == c.ActorId || owner == c.ActorId), mentions);
    }
    public async Task<CommentPage> ListAsync(WorkspaceReadContext c, long taskId, int page, CancellationToken token = default)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var access = await Require(c, taskId, token);
        if (page is < 1 or > 51) throw new TaskValidationException("page", "Choose a page from 1 to 51.");
        var query = Rows(c, taskId).AsNoTracking(); var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(row => row.Id).Skip((page - 1) * 20).Take(20).ToArrayAsync(token);
        var items = new List<TaskCommentDto>(); foreach (var row in rows) items.Add(await Map(c, row, access.Owner, access.Write, token));
        await snapshot.CommitAsync(token); return new(items.ToArray(), page, 20, total, (int)Math.Ceiling(total / 20d), access.Write);
    }
    public async Task<CommentCandidatesDto> CandidatesAsync(WorkspaceReadContext c, long taskId, string search, CancellationToken token = default)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var access = await Require(c, taskId, token); var text = search.Trim();
        if (text.Length is < 2 or > 100) throw new TaskValidationException("search", "Enter 2 to 100 characters of a collaborator name.");
        var rows = await People(c.WorkspaceId, access.Task.ProjectId, access.Owner).Where(u => u.DisplayName.Contains(text)).OrderBy(u => u.DisplayName).ThenBy(u => u.Id).Take(10).Select(u => new CommentMentionDto(Id(u.Id), u.DisplayName)).ToArrayAsync(token);
        await snapshot.CommitAsync(token); return new(rows);
    }
    public async Task<CommentHistoryDto> HistoryAsync(WorkspaceReadContext c, long taskId, long commentId, CancellationToken token = default)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token); await Require(c, taskId, token);
        if (!await Rows(c, taskId).AnyAsync(row => row.Id == commentId, token)) throw Missing();
        var rows = await (from revision in db.CommentRevisions.AsNoTracking() join user in db.Users on revision.ActorId equals user.Id
            where revision.WorkspaceId == c.WorkspaceId && revision.CommentId == commentId orderby revision.Version descending
            select new CommentRevisionDto(Id(revision.Version), revision.Action, user.DisplayName, revision.CreatedAtUtc)).Take(101).ToArrayAsync(token);
        await snapshot.CommitAsync(token); return new(rows);
    }
    public Task<TaskCommentDto> CreateAsync(WorkspaceReadContext c, long task, string body, CancellationToken token = default) => Write(c, task, null, body, null, false, token);
    public Task<TaskCommentDto> EditAsync(WorkspaceReadContext c, long task, long comment, string body, long expected, CancellationToken token = default) => Write(c, task, comment, body, expected, false, token);
    public async Task DeleteAsync(WorkspaceReadContext c, long task, long comment, long expected, CancellationToken token = default) => _ = await Write(c, task, comment, null, expected, true, token);
    private async Task<TaskCommentDto> Write(WorkspaceReadContext c, long taskId, long? commentId, string? body, long? expected, bool delete, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var companies = await db.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={c.WorkspaceId} FOR UPDATE").ToArrayAsync(token);
        if (companies.Length != 1) throw Missing();
        var access = await Require(c, taskId, token);
        if (!access.Write) throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
        TaskComment row; var oldMentions = Array.Empty<long>(); var now = clock.GetUtcNow().UtcDateTime;
        if (commentId is { } id)
        {
            row = await Rows(c, taskId).SingleOrDefaultAsync(row => row.Id == id, token) ?? throw Missing();
            if (row.DeletedAtUtc != null) throw Missing();
            if (row.AuthorId != c.ActorId && (!delete || access.Owner != c.ActorId)) throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
            if (expected != row.Version) throw new ResourceVersionException(409, "resource.conflict");
            if (row.Version >= 100 && !delete) throw new TaskValidationException("body", "This comment has reached its revision limit.");
            oldMentions = CommentMentions.Parse(row.Body);
        }
        else
        {
            if (await Rows(c, taskId).CountAsync(token) >= 1000) throw new TaskValidationException("body", "This task has reached its comment limit.");
            row = new() { WorkspaceId = c.WorkspaceId, ProjectId = access.Task.ProjectId, TaskId = taskId, AuthorId = c.ActorId, CreatedAtUtc = now }; db.TaskComments.Add(row);
        }
        long[] mentions = [];
        if (delete) { row.Body = ""; row.DeletedAtUtc = now; }
        else
        {
            var text = body?.Trim() ?? "";
            if (text.Length is < 1 or > 5000 || text.Any(ch => char.IsControl(ch) && ch is not '\n' and not '\r' and not '\t')) throw new TaskValidationException("body", "Enter 1 to 5000 characters of plain text.");
            mentions = CommentMentions.Parse(text);
            var eligible = await People(c.WorkspaceId, row.ProjectId, access.Owner).CountAsync(u => mentions.Contains(u.Id), token);
            if (eligible != mentions.Length) throw new TaskValidationException("body", "Choose a current project collaborator for each mention.");
            if (commentId != null && text == row.Body) { await transaction.CommitAsync(token); return await Map(c, row, access.Owner, access.Write, token); }
            row.Body = text; if (commentId != null) row.EditedAtUtc = now;
        }
        await db.SaveChangesAsync(token);
        var action = delete ? "Deleted" : commentId == null ? "Created" : "Edited";
        db.CommentRevisions.Add(new() { WorkspaceId = c.WorkspaceId, CommentId = row.Id, ActorId = c.ActorId, Version = row.Version, Action = action, CreatedAtUtc = now });
        var message = new OutboxEvent { WorkspaceId = c.WorkspaceId, ProjectId = row.ProjectId, TaskId = taskId, ActorUserId = c.ActorId, Kind = "Comment." + action, CreatedAtUtc = now, CorrelationId = AuditCorrelation.Current() };
        db.OutboxDeliveries.Add(new() { WorkspaceId = c.WorkspaceId, Event = message });
        if (db.NotificationsEnabled && !delete)
        {
            var targets = mentions.Except(oldMentions).Where(id => id != c.ActorId).ToArray();
            var muted = await db.NotificationPreferences.Where(p => p.WorkspaceId == c.WorkspaceId && targets.Contains(p.UserId) && !p.InAppEnabled).Select(p => p.UserId).ToArrayAsync(token);
            foreach (var recipient in targets.Except(muted)) db.UserNotifications.Add(new() { WorkspaceId = c.WorkspaceId, ProjectId = row.ProjectId, TaskId = taskId, CommentId = row.Id,
                RecipientUserId = recipient, Kind = "TaskMention", Event = message, CreatedAtUtc = now });
        }
        if (!delete && commentId is null) await WatcherNotificationDelivery.CaptureAsync(db, message, row.Id, token);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return await Map(c, row, access.Owner, access.Write, token);
    }
}
