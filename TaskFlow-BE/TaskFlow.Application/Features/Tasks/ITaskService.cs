using TaskFlow.Domain.Entities;
namespace TaskFlow.Application.Features.Tasks;

public sealed record TaskDto(string Id, string ProjectId, string Title, string? Description, string Status, string Priority,
    string? AssigneeId, DateTime? DueDateUtc, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record TaskPage(IReadOnlyList<TaskDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record CreateTask(string Title, string? Description, TaskPriority Priority, long? AssigneeId, DateTime? DueDateUtc);
public readonly record struct FieldChange<T>(bool IsSpecified, T Value);
public sealed record TaskPatch(FieldChange<string?> Title = default, FieldChange<string?> Description = default,
    FieldChange<TaskPriority> Priority = default, FieldChange<long?> AssigneeId = default, FieldChange<DateTime?> DueDateUtc = default);
public sealed record TaskStatusDto(string Id, string Status, DateTime UpdatedAtUtc);
public sealed record TaskListOptions(int Page = 1, int PageSize = 20, string? Search = null, TaskState? Status = null,
    TaskPriority? Priority = null, long? AssigneeId = null, string SortBy = "createdAtUtc", string SortDirection = "desc");
public sealed class TaskValidationException(string field, string message) : Exception(message) { public string Field { get; } = field; }
public interface ITaskService
{
    Task<TaskDto> UpdateAsync(long? actorId, long taskId, TaskPatch patch, CancellationToken cancellationToken = default);
    Task<TaskStatusDto> SetStatusAsync(long? actorId, long taskId, TaskState status, CancellationToken cancellationToken = default);
    Task DeleteAsync(long? actorId, long taskId, CancellationToken cancellationToken = default);
    Task<TaskDto> CreateAsync(long? actorId, long projectId, CreateTask request, CancellationToken cancellationToken = default);
    Task<TaskPage> ListAsync(long? actorId, long projectId, TaskListOptions options, CancellationToken cancellationToken = default);
    Task<TaskDto> GetAsync(long? actorId, long taskId, CancellationToken cancellationToken = default);
}
