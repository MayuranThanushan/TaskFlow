using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Projects;

public sealed class ProjectAccess(TaskFlowDbContext database) : IProjectAccess
{
    public async Task<ProjectAccessDecision> CheckAsync(long? actorId, long projectId, ProjectOperation operation,
        CancellationToken cancellationToken = default)
    {
        if (actorId is null or <= 0) return ProjectAccessDecision.Unauthenticated;
        var role = await RoleAsync(database, actorId.Value, projectId, cancellationToken);
        return ProjectAccessPolicy.Decide(actorId, role, operation);
    }

    public async Task<ProjectAccessDecision> CheckTaskAsync(long? actorId, long taskId, ProjectOperation operation,
        CancellationToken cancellationToken = default)
    {
        if (actorId is null or <= 0) return ProjectAccessDecision.Unauthenticated;
        var projectId = await database.Tasks.AsNoTracking().Where(task => task.Id == taskId)
            .Select(task => (long?)task.ProjectId).SingleOrDefaultAsync(cancellationToken);
        return projectId is null ? ProjectAccessDecision.NotFound
            : await CheckAsync(actorId, projectId.Value, operation, cancellationToken);
    }

    internal static async Task<ProjectRole?> RoleAsync(TaskFlowDbContext database, long actorId, long projectId,
        CancellationToken cancellationToken)
    {
        var project = await VisibleProjects(database, actorId).AsNoTracking().Where(project => project.Id == projectId)
            .Select(project => new { Owner = project.OwnerId == actorId }).SingleOrDefaultAsync(cancellationToken);
        return project is null ? null : project.Owner ? ProjectRole.Owner : ProjectRole.Member;
    }

    internal static IQueryable<Project> VisibleProjects(TaskFlowDbContext database, long actorId) =>
        database.Projects.Where(project => actorId > 0 && (project.OwnerId == actorId
            || database.ProjectMembers.Any(member => member.ProjectId == project.Id
                && member.UserId == actorId && member.Role == MembershipRole.Member)));
}
