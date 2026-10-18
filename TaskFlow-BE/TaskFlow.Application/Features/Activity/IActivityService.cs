namespace TaskFlow.Application.Features.Activity;

public sealed record ActivityDto(string Id, string ProjectId, string ActorUserId, string? TaskId, string ActionType, string Details, DateTime CreatedAtUtc);
public sealed record ActivityPage(IReadOnlyList<ActivityDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record ActivityOptions(int Page = 1, int PageSize = 20, DateTime? FromUtc = null, DateTime? ToUtc = null);
public interface IActivityService
{
    Task<ActivityPage> ListAsync(long? actorId, long projectId, ActivityOptions options, CancellationToken cancellationToken = default);
}
