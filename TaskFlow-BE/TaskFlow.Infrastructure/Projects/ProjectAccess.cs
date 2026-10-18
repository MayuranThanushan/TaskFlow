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
        var project = await database.Projects.AsNoTracking().Where(project => project.Id == projectId)
            .Select(project => new
            {
                Owner = project.OwnerId == actorId,
                Member = database.ProjectMembers.Any(member => member.ProjectId == project.Id
                    && member.UserId == actorId && member.Role == MembershipRole.Member)
            }).SingleOrDefaultAsync(cancellationToken);
        return project?.Owner == true ? ProjectRole.Owner : project?.Member == true ? ProjectRole.Member : null;
    }
}
