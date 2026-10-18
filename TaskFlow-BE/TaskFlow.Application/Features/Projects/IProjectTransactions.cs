using TaskFlow.Domain.Entities;

namespace TaskFlow.Application.Features.Projects;

// Internal use-case boundary, never a public request model. Actor identity comes from the session.
public interface IProjectTransactions
{
    Task<T> CreateAsync<T>(long? actorId, Func<IProjectMutation, CancellationToken, Task<T>> mutation,
        CancellationToken cancellationToken = default);
    Task<T> ExecuteAsync<T>(long? actorId, long projectId, ProjectOperation operation,
        Func<IProjectMutation, CancellationToken, Task<T>> mutation, CancellationToken cancellationToken = default);
}

public interface IProjectMutation
{
    Project Project { get; }
    Task<TaskItem> CreateTaskAsync(TaskFlow.Application.Features.Tasks.CreateTask request, CancellationToken cancellationToken = default);
    Task<(ProjectMember Member, string DisplayName)> AddMemberAsync(long userId, CancellationToken cancellationToken = default);
    Task RemoveMemberAsync(long userId, CancellationToken cancellationToken = default);
    void Record(ActivityAction action, IReadOnlyDictionary<string, string?>? details = null, long? taskId = null);
    Task<bool> IsEligibleAssigneeAsync(long userId, CancellationToken cancellationToken = default);
}
