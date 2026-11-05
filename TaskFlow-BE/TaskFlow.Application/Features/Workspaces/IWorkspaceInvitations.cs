namespace TaskFlow.Application.Features.Workspaces;
public sealed record InvitationDto(string Id, string WorkspaceId, string? ProjectId, string Email, string Role, string State, string Version, DateTime ExpiresAtUtc, int Resends, string DeliveryState);
public sealed record InvitationPage(InvitationDto[] Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record InvitationPreviewDto(string WorkspaceId, string WorkspaceName, string? ProjectId, string? ProjectName, string Role, DateTime ExpiresAtUtc);
public interface IWorkspaceInvitations
{
    Task<InvitationPage> ListAsync(WorkspaceWriteContext context, long? projectId, int page, int size, CancellationToken token);
    Task<InvitationDto> CreateAsync(WorkspaceWriteContext context, string email, long? projectId, string role, CancellationToken token);
    Task<InvitationDto> ResendAsync(WorkspaceWriteContext context, long invitationId, long expectedVersion, CancellationToken token);
    Task RevokeAsync(WorkspaceWriteContext context, long invitationId, long expectedVersion, CancellationToken token);
    Task<InvitationPreviewDto> PreviewAsync(WorkspaceWriteContext context, long invitationId, string secret, bool accept, CancellationToken token);
}
public sealed class InvitationException(int status, string code) : Exception("Invitation action could not be completed.")
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
