using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Workspaces;

// Capacity admission must run after authorization under the same workspace gate as the insert/reactivation.
public static class PilotQuotas
{
    public const int Companies = 10, OwnedCompanies = 3, Members = 25, Projects = 20, Tasks = 2000, ProjectTasks = 500,
        RetainedProjects = 100, RetainedTasks = 10000;
    public static void Deny() => throw new PilotQuotaException();
    public static async Task ProjectAsync(TaskFlowDbContext db, long workspace, CancellationToken token)
    {
        var all = db.Projects.IgnoreQueryFilters().Where(p => p.WorkspaceId == workspace);
        if (await all.CountAsync(token) >= RetainedProjects || await all.CountAsync(p => p.DeletedAtUtc == null, token) >= Projects) Deny();
    }
    public static async Task TaskAsync(TaskFlowDbContext db, long workspace, long project, CancellationToken token)
    {
        var all = db.Tasks.IgnoreQueryFilters().Where(t => t.WorkspaceId == workspace && t.Project.WorkspaceId == workspace);
        var active = all.Where(t => t.DeletedAtUtc == null && t.Project.DeletedAtUtc == null);
        if (await all.CountAsync(token) >= RetainedTasks || await active.CountAsync(token) >= Tasks
            || await active.CountAsync(t => t.ProjectId == project, token) >= ProjectTasks) Deny();
    }
    public static async Task MemberAsync(TaskFlowDbContext db, long workspace, CancellationToken token)
    {
        if (await db.WorkspaceMemberships.CountAsync(m => m.WorkspaceId == workspace && m.State == WorkspaceMembershipState.Active, token) >= Members) Deny();
    }
}
