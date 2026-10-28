using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Application.Features.Dashboard;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Infrastructure.Projects;

namespace TaskFlow.Infrastructure.Workspaces;

public sealed class WorkspaceReads(TaskFlowDbContext database, TimeProvider clock) : IWorkspaceReads
{
    private static string Id(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static ProjectAccessException Missing() => new(ProjectAccessDecision.NotFound);
    private static void Page(int page, int size)
    {
        if (page < 1 || size is < 1 or > 100 || (long)(page - 1) * size > int.MaxValue)
            throw new ArgumentException("Invalid page.");
    }
    public async Task AuthorizeAsync(WorkspaceReadContext context, CancellationToken token = default)
    {
        if (context.ActorId <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        if (context.WorkspaceId <= 0 || !await database.WorkspaceMemberships.AsNoTracking().AnyAsync(m =>
            m.WorkspaceId == context.WorkspaceId && m.UserId == context.ActorId && m.State == WorkspaceMembershipState.Active, token)) throw Missing();
    }
    // History includes deleted tasks through IgnoreQueryFilters. EF can propagate that flag
    // across composed subqueries, so authorization and parent deletion are explicit predicates.
    private IQueryable<Project> Projects(WorkspaceReadContext context) => database.Projects.AsNoTracking().Where(p =>
        p.WorkspaceId == context.WorkspaceId && p.DeletedAtUtc == null
        && database.WorkspaceMemberships.Any(m => m.WorkspaceId == context.WorkspaceId && m.UserId == context.ActorId && m.State == WorkspaceMembershipState.Active)
        && database.WorkspaceMemberships.Any(m => m.WorkspaceId == context.WorkspaceId && m.UserId == p.OwnerId)
        && (p.OwnerId == context.ActorId || database.ProjectMembers.Any(m => m.WorkspaceId == context.WorkspaceId
            && m.ProjectId == p.Id && m.UserId == context.ActorId && m.Role == MembershipRole.Member)));
    private IQueryable<TaskItem> Tasks(WorkspaceReadContext context)
    {
        var projects = Projects(context);
        return database.Tasks.AsNoTracking().Where(t => t.WorkspaceId == context.WorkspaceId && t.DeletedAtUtc == null
            && t.Project.DeletedAtUtc == null && projects.Any(p => p.Id == t.ProjectId)
            && (t.AssigneeId == null || database.WorkspaceMemberships.Any(m => m.WorkspaceId == context.WorkspaceId && m.UserId == t.AssigneeId)
                && (t.Project.OwnerId == t.AssigneeId || database.ProjectMembers.Any(m => m.WorkspaceId == context.WorkspaceId && m.ProjectId == t.ProjectId && m.UserId == t.AssigneeId))));
    }
    private async Task<T> Read<T>(WorkspaceReadContext context, Func<Task<T>> query, CancellationToken token)
    {
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await AuthorizeAsync(context, token);
        var result = await query();
        await snapshot.CommitAsync(token);
        return result;
    }
    private async Task<Project> RequireProject(WorkspaceReadContext context, long projectId, CancellationToken token) =>
        await Projects(context).SingleOrDefaultAsync(p => p.Id == projectId, token) ?? throw Missing();

    public Task<WorkspaceDto> WorkspaceAsync(WorkspaceReadContext context, CancellationToken token = default) => Read(context, async () =>
    {
        var workspace = await database.Workspaces.AsNoTracking().SingleAsync(w => w.Id == context.WorkspaceId, token);
        var membership = await database.WorkspaceMemberships.AsNoTracking().SingleAsync(m => m.WorkspaceId == context.WorkspaceId && m.UserId == context.ActorId, token);
        return new WorkspaceDto(Id(workspace.Id), workspace.Name, membership.Role.ToString());
    }, token);

    public async Task<WorkspacePage> WorkspacesAsync(long actorId, int page, int pageSize, CancellationToken token = default)
    {
        if (actorId <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        Page(page, pageSize);
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var query = from m in database.WorkspaceMemberships.AsNoTracking()
                    join w in database.Workspaces.AsNoTracking() on m.WorkspaceId equals w.Id
                    where m.UserId == actorId && m.State == WorkspaceMembershipState.Active
                    orderby w.Id
                    select new { w.Id, w.Name, m.Role };
        var total = await query.CountAsync(token);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(token);
        await snapshot.CommitAsync(token);
        return new(rows.Select(r => new WorkspaceDto(Id(r.Id), r.Name, r.Role.ToString())).ToArray(), page, pageSize, total, (int)Math.Ceiling(total / (double)pageSize));
    }
    public Task<ProjectPage> ProjectsAsync(WorkspaceReadContext context, ProjectListOptions options, CancellationToken token = default) => Read(context, async () =>
    {
        Page(options.Page, options.PageSize);
        if (options.Search?.Length > 200 || options.SortBy is not ("name" or "createdAtUtc" or "updatedAtUtc") || options.SortDirection is not ("asc" or "desc")) throw new ArgumentException("Invalid project filters.");
        var query = Projects(context); var search = options.Search?.Trim();
        if (!string.IsNullOrEmpty(search)) query = query.Where(p => p.Name.Contains(search));
        var total = await query.CountAsync(token); var asc = options.SortDirection == "asc";
        var ordered = options.SortBy switch
        {
            "name" => asc ? query.OrderBy(p => p.Name) : query.OrderByDescending(p => p.Name),
            "updatedAtUtc" => asc ? query.OrderBy(p => p.UpdatedAtUtc) : query.OrderByDescending(p => p.UpdatedAtUtc),
            _ => asc ? query.OrderBy(p => p.CreatedAtUtc) : query.OrderByDescending(p => p.CreatedAtUtc)
        };
        var rows = await ordered.ThenByDescending(p => p.Id).Skip((options.Page - 1) * options.PageSize).Take(options.PageSize).ToArrayAsync(token);
        return new ProjectPage(rows.Select(ProjectService.Map).ToArray(), options.Page, options.PageSize, total, (int)Math.Ceiling(total / (double)options.PageSize));
    }, token);
    public Task<ProjectDetailsDto> ProjectAsync(WorkspaceReadContext context, long projectId, CancellationToken token = default) => Read(context, async () =>
    {
        var project = await RequireProject(context, projectId, token);
        var totals = await Tasks(context).Where(t => t.ProjectId == projectId).GroupBy(t => 1)
            .Select(g => new { Total = g.Count(), Done = g.Count(t => t.Status == TaskState.Done) }).SingleOrDefaultAsync(token);
        var dto = ProjectService.Map(project); var total = totals?.Total ?? 0; var done = totals?.Done ?? 0;
        return new ProjectDetailsDto(dto.Id, dto.Name, dto.Description, dto.OwnerId, dto.CreatedAtUtc, dto.UpdatedAtUtc,
            total, done, total == 0 ? 0 : (int)Math.Round(done * 100m / total, MidpointRounding.AwayFromZero));
    }, token);
    public Task<TaskDto> TaskAsync(WorkspaceReadContext context, long taskId, CancellationToken token = default) => Read(context, async () =>
        TaskService.Map(await Tasks(context).SingleOrDefaultAsync(t => t.Id == taskId, token) ?? throw Missing()), token);
    public Task<TaskPage> TasksAsync(WorkspaceReadContext context, long projectId, TaskListOptions options, CancellationToken token = default) => Read(context, async () =>
    {
        await RequireProject(context, projectId, token); Page(options.Page, options.PageSize);
        if (options.Search?.Length > 200 || options.SortBy is not ("title" or "priority" or "dueDateUtc" or "createdAtUtc") || options.SortDirection is not ("asc" or "desc")
            || options.Status is { } s && !Enum.IsDefined(s) || options.Priority is { } p && !Enum.IsDefined(p) || options.AssigneeId is <= 0) throw new ArgumentException("Invalid task filters.");
        var query = Tasks(context).Where(t => t.ProjectId == projectId); var search = options.Search?.Trim();
        if (!string.IsNullOrEmpty(search)) query = query.Where(t => t.Title.Contains(search));
        if (options.Status is { } status) query = query.Where(t => t.Status == status);
        if (options.Priority is { } priority) query = query.Where(t => t.Priority == priority);
        if (options.AssigneeId is { } assignee) query = query.Where(t => t.AssigneeId == assignee);
        var total = await query.CountAsync(token); var asc = options.SortDirection == "asc";
        var ordered = options.SortBy switch
        {
            "title" => asc ? query.OrderBy(t => t.Title) : query.OrderByDescending(t => t.Title),
            "priority" => asc ? query.OrderBy(t => t.Priority == TaskPriority.Low ? 0 : t.Priority == TaskPriority.Medium ? 1 : 2) : query.OrderByDescending(t => t.Priority == TaskPriority.Low ? 0 : t.Priority == TaskPriority.Medium ? 1 : 2),
            "dueDateUtc" => asc ? query.OrderBy(t => t.DueDateUtc == null).ThenBy(t => t.DueDateUtc) : query.OrderBy(t => t.DueDateUtc == null).ThenByDescending(t => t.DueDateUtc),
            _ => asc ? query.OrderBy(t => t.CreatedAtUtc) : query.OrderByDescending(t => t.CreatedAtUtc)
        };
        var rows = await ordered.ThenByDescending(t => t.Id).Skip((options.Page - 1) * options.PageSize).Take(options.PageSize).ToArrayAsync(token);
        return new TaskPage(rows.Select(TaskService.Map).ToArray(), options.Page, options.PageSize, total, (int)Math.Ceiling(total / (double)options.PageSize));
    }, token);
    public Task<ProjectMemberList> MembersAsync(WorkspaceReadContext context, long projectId, CancellationToken token = default) => Read(context, async () =>
    {
        var project = await RequireProject(context, projectId, token);
        var rows = await (from m in database.WorkspaceMemberships.AsNoTracking()
                          join u in database.Users.AsNoTracking() on m.UserId equals u.Id
                          where m.WorkspaceId == context.WorkspaceId && m.State == WorkspaceMembershipState.Active
                              && (u.Id == project.OwnerId || database.ProjectMembers.Any(pm => pm.WorkspaceId == context.WorkspaceId && pm.ProjectId == projectId && pm.UserId == u.Id))
                          orderby u.Id
                          select new { u.Id, u.DisplayName, JoinedAtUtc = u.Id == project.OwnerId ? project.CreatedAtUtc : database.ProjectMembers.Where(pm => pm.WorkspaceId == context.WorkspaceId && pm.ProjectId == projectId && pm.UserId == u.Id).Select(pm => pm.JoinedAtUtc).First() }).ToArrayAsync(token);
        return new ProjectMemberList(rows.OrderBy(r => r.Id == project.OwnerId ? 0 : 1).Select(r => new ProjectMemberDto(Id(r.Id), r.DisplayName,
            r.Id == project.OwnerId ? "Owner" : "Member", r.Id == project.OwnerId ? project.CreatedAtUtc : r.JoinedAtUtc)).ToArray());
    }, token);
    public Task<ActivityPage> ActivityAsync(WorkspaceReadContext context, long? projectId, ActivityOptions options, CancellationToken token = default) => Read(context, async () =>
    {
        if (projectId is { } id) await RequireProject(context, id, token);
        Page(options.Page, options.PageSize);
        if (options.FromUtc > options.ToUtc) throw new ArgumentException("Invalid activity range.");
        var projects = Projects(context);
        var query = database.ActivityLogs.AsNoTracking().Where(a => a.WorkspaceId == context.WorkspaceId
            && projects.Any(p => p.Id == a.ProjectId) && (projectId == null || a.ProjectId == projectId)
            && (a.TaskId == null || database.Tasks.IgnoreQueryFilters().Any(t => t.Id == a.TaskId && t.ProjectId == a.ProjectId && t.WorkspaceId == context.WorkspaceId)));
        if (options.FromUtc is { } from)
        {
            var extra = (10 - from.Ticks % 10) % 10;
            if (from.Ticks > DateTime.MaxValue.Ticks - extra) query = query.Where(a => false);
            else { var lower = new DateTime(from.Ticks + extra, DateTimeKind.Utc); query = query.Where(a => a.CreatedAtUtc >= lower); }
        }
        if (options.ToUtc is { } to) { var upper = new DateTime(to.Ticks - to.Ticks % 10, DateTimeKind.Utc); query = query.Where(a => a.CreatedAtUtc <= upper); }
        var total = await query.CountAsync(token);
        var rows = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Skip((options.Page - 1) * options.PageSize).Take(options.PageSize).ToArrayAsync(token);
        return new ActivityPage(rows.Select(a => new ActivityDto(Id(a.Id), Id(a.ProjectId), Id(a.ActorUserId), a.TaskId is { } task ? Id(task) : null,
            a.ActionType.ToString(), ActivityService.Summary(a), a.CreatedAtUtc)).ToArray(), options.Page, options.PageSize, total, (int)Math.Ceiling(total / (double)options.PageSize));
    }, token);
    public Task<DashboardDto> DashboardAsync(WorkspaceReadContext context, CancellationToken token = default) => Read(context, async () =>
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var projects = await Projects(context).CountAsync(token);
        var totals = await Tasks(context).GroupBy(t => 1).Select(g => new
        {
            Total = g.Count(), ToDo = g.Count(t => t.Status == TaskState.ToDo), InProgress = g.Count(t => t.Status == TaskState.InProgress), Done = g.Count(t => t.Status == TaskState.Done),
            Assigned = g.Count(t => t.AssigneeId == context.ActorId), Overdue = g.Count(t => t.DueDateUtc != null && t.DueDateUtc < now && t.Status != TaskState.Done)
        }).SingleOrDefaultAsync(token);
        return new DashboardDto(projects, totals?.Total ?? 0, new(totals?.ToDo ?? 0, totals?.InProgress ?? 0, totals?.Done ?? 0), totals?.Assigned ?? 0, totals?.Overdue ?? 0);
    }, token);
}
