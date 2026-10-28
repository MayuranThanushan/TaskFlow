namespace TaskFlow.Application.Features.Projects;

public enum ProjectRole { Owner, Member, Viewer }
public enum ProjectOperation { Read, ManageProject, ManageMembers, WriteTasks, DeleteTasks }
public enum ProjectAccessDecision { Allowed, Unauthenticated, NotFound, Forbidden }

public static class ProjectAccessPolicy
{
    public static ProjectAccessDecision Decide(long? actorId, ProjectRole? role, ProjectOperation operation)
    {
        if (actorId is null or <= 0) return ProjectAccessDecision.Unauthenticated;
        if (!Enum.IsDefined(operation)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (role is not (ProjectRole.Owner or ProjectRole.Member or ProjectRole.Viewer)) return ProjectAccessDecision.NotFound;
        return operation switch
        {
            ProjectOperation.Read => ProjectAccessDecision.Allowed,
            ProjectOperation.WriteTasks => role == ProjectRole.Viewer ? ProjectAccessDecision.Forbidden : ProjectAccessDecision.Allowed,
            ProjectOperation.ManageProject or ProjectOperation.ManageMembers or ProjectOperation.DeleteTasks
                => role == ProjectRole.Owner ? ProjectAccessDecision.Allowed : ProjectAccessDecision.Forbidden,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
    }
}

public interface IProjectAccess
{
    Task<ProjectAccessDecision> CheckAsync(long? actorId, long projectId, ProjectOperation operation,
        CancellationToken cancellationToken = default);
    Task<ProjectAccessDecision> CheckTaskAsync(long? actorId, long taskId, ProjectOperation operation,
        CancellationToken cancellationToken = default);
}

public sealed class ProjectAccessException : Exception
{
    public ProjectAccessDecision Decision { get; }
    public ProjectAccessException(ProjectAccessDecision decision) : base("Project access was denied.")
    {
        if (decision == ProjectAccessDecision.Allowed || !Enum.IsDefined(decision))
            throw new ArgumentOutOfRangeException(nameof(decision));
        Decision = decision;
    }
}
