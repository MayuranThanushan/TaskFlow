using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Notifications;
internal static class WatcherAccess
{
    public static IQueryable<TaskItem> Tasks(TaskFlowDbContext db, WorkspaceReadContext c) => db.Tasks.AsNoTracking().Where(t => t.WorkspaceId == c.WorkspaceId && t.Project.WorkspaceId == c.WorkspaceId && t.DeletedAtUtc == null && t.Project.DeletedAtUtc == null
        && db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == c.ActorId && m.State == WorkspaceMembershipState.Active)
        && db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == t.Project.OwnerId)
        && (t.Project.OwnerId == c.ActorId || db.ProjectMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.ProjectId == t.ProjectId && m.UserId == c.ActorId && m.RevokedAtUtc == null))
        && (t.AssigneeId == null || db.WorkspaceMemberships.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == t.AssigneeId)
            && (t.Project.OwnerId == t.AssigneeId || db.ProjectMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.ProjectId == t.ProjectId && m.UserId == t.AssigneeId && m.RevokedAtUtc == null && m.Role != MembershipRole.Viewer))));
}
