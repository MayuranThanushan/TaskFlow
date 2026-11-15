using TaskFlow.Application.Features.Workspaces;
namespace TaskFlow.Application.Features.Comments;
public sealed record CommentMentionDto(string UserId, string DisplayName);
public sealed record CommentCandidatesDto(CommentMentionDto[] Items);
public sealed record TaskCommentDto(string Id, string TaskId, string AuthorId, string AuthorName, string? Body, string Version, DateTime CreatedAtUtc, DateTime? EditedAtUtc, DateTime? DeletedAtUtc, bool CanEdit, bool CanDelete, CommentMentionDto[] Mentions);
public sealed record CommentPage(TaskCommentDto[] Items, int Page, int PageSize, int TotalCount, int TotalPages, bool CanComment);
public sealed record CommentRevisionDto(string Version, string Action, string ActorName, DateTime CreatedAtUtc);
public sealed record CommentHistoryDto(CommentRevisionDto[] Items);
public interface ITaskComments
{
    Task<CommentPage> ListAsync(WorkspaceReadContext context, long taskId, int page, CancellationToken token = default);
    Task<CommentCandidatesDto> CandidatesAsync(WorkspaceReadContext context, long taskId, string search, CancellationToken token = default);
    Task<CommentHistoryDto> HistoryAsync(WorkspaceReadContext context, long taskId, long commentId, CancellationToken token = default);
    Task<TaskCommentDto> CreateAsync(WorkspaceReadContext context, long taskId, string body, CancellationToken token = default);
    Task<TaskCommentDto> EditAsync(WorkspaceReadContext context, long taskId, long commentId, string body, long expectedVersion, CancellationToken token = default);
    Task DeleteAsync(WorkspaceReadContext context, long taskId, long commentId, long expectedVersion, CancellationToken token = default);
}
