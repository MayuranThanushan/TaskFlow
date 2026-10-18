namespace TaskFlow.Application.Features.Projects;

public sealed record ProjectDto(string Id, string Name, string? Description, string OwnerId, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record ProjectDetailsDto(string Id, string Name, string? Description, string OwnerId,
    DateTime CreatedAtUtc, DateTime UpdatedAtUtc, int TotalTasks, int CompletedTasks, int ProgressPercentage);
public sealed record ProjectPage(IReadOnlyList<ProjectDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record ProjectListOptions(int Page = 1, int PageSize = 20, string? Search = null,
    string SortBy = "createdAtUtc", string SortDirection = "desc");

public interface IProjectService
{
    Task<ProjectDto> CreateAsync(long? actorId, string name, string? description, CancellationToken cancellationToken = default);
    Task<ProjectPage> ListAsync(long? actorId, ProjectListOptions options, CancellationToken cancellationToken = default);
    Task<ProjectDetailsDto> GetAsync(long? actorId, long projectId, CancellationToken cancellationToken = default);
}
