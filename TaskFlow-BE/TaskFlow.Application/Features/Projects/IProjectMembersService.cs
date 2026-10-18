namespace TaskFlow.Application.Features.Projects;

public sealed record ProjectMemberDto(string UserId, string DisplayName, string Role, DateTime JoinedAtUtc);
public sealed record ProjectMemberList(IReadOnlyList<ProjectMemberDto> Items);
public enum MembershipFailure { NotFound, Conflict }
public sealed class MembershipException(MembershipFailure failure) : Exception
{
    public MembershipFailure Failure { get; } = failure;
}
public interface IProjectMembersService
{
    Task<ProjectMemberList> ListAsync(long? actorId, long projectId, CancellationToken cancellationToken = default);
    Task<ProjectMemberDto> AddAsync(long? actorId, long projectId, long userId, CancellationToken cancellationToken = default);
    Task RemoveAsync(long? actorId, long projectId, long userId, CancellationToken cancellationToken = default);
}
