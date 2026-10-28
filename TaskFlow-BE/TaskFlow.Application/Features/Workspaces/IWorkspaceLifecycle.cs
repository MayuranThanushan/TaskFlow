using TaskFlow.Domain.Entities;

namespace TaskFlow.Application.Features.Workspaces;

public sealed record WorkspaceMemberDto(string UserId, string DisplayName, string? Role, string? State);
public sealed record WorkspaceMemberPage(IReadOnlyList<WorkspaceMemberDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record WorkspaceAuditDto(string Id, string ActorUserId, string TargetUserId, string? ProjectId, string Action, string? FromRole, string? ToRole, DateTime CreatedAtUtc);
public sealed record WorkspaceAuditPage(IReadOnlyList<WorkspaceAuditDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public interface IWorkspaceLifecycle
{
    Task<WorkspaceMemberPage> MembersAsync(WorkspaceReadContext context, int page, int size, CancellationToken token = default);
    Task<WorkspaceAuditPage> AuditAsync(WorkspaceReadContext context, int page, int size, CancellationToken token = default);
    Task SetRoleAsync(WorkspaceWriteContext context, long userId, WorkspaceRole role, string? password, CancellationToken token = default);
    Task SetStateAsync(WorkspaceWriteContext context, long userId, WorkspaceMembershipState state, CancellationToken token = default);
    Task LeaveAsync(WorkspaceWriteContext context, string? password, CancellationToken token = default);
    Task TransferCompanyAsync(WorkspaceWriteContext context, long userId, string password, CancellationToken token = default);
    Task SetProjectRoleAsync(WorkspaceWriteContext context, long projectId, long userId, MembershipRole role, CancellationToken token = default);
    Task TransferProjectAsync(WorkspaceWriteContext context, long projectId, long userId, string password, CancellationToken token = default);
}
