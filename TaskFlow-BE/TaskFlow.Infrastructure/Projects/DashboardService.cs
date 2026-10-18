using System.Data;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Dashboard;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Projects;

public sealed class DashboardService(TaskFlowDbContext database, TimeProvider clock) : IDashboardService
{
    public async Task<DashboardDto> GetAsync(long? actorId, CancellationToken cancellationToken = default)
    {
        if (actorId is null or <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        var actor = actorId.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var projects = ProjectAccess.VisibleProjects(database, actor).AsNoTracking();
        // Both aggregates see the same scope even if projects/tasks change between statements.
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var totalProjects = await projects.CountAsync(cancellationToken);
        var totals = await database.Tasks.AsNoTracking().Where(t => projects.Any(p => p.Id == t.ProjectId))
            .GroupBy(t => 1).Select(group => new
            {
                Total = group.Count(), ToDo = group.Count(t => t.Status == TaskState.ToDo),
                InProgress = group.Count(t => t.Status == TaskState.InProgress), Done = group.Count(t => t.Status == TaskState.Done),
                Assigned = group.Count(t => t.AssigneeId == actor),
                Overdue = group.Count(t => t.DueDateUtc != null && t.DueDateUtc < now && t.Status != TaskState.Done)
            }).SingleOrDefaultAsync(cancellationToken);
        await snapshot.CommitAsync(cancellationToken);
        return new(totalProjects, totals?.Total ?? 0, new(totals?.ToDo ?? 0, totals?.InProgress ?? 0, totals?.Done ?? 0), totals?.Assigned ?? 0, totals?.Overdue ?? 0);
    }
}
