using TaskFlow.Application.Features.Activity;

namespace TaskFlow.Application.Features.Workspaces;

public sealed record AuditConsoleOptions(string Source = "Governance", long? ActorId = null, string? Action = null,
    string? EntityType = null, string? EntityId = null, DateTime? FromUtc = null, DateTime? ToUtc = null,
    string? Outcome = null, string ResourceState = "All");
public sealed record AuditConsoleRowDto(string Id, string Source, string ActorUserId, string? ProjectId,
    string Action, DateTime CreatedAtUtc, bool? ProjectDeleted, AuditMetadataDto Audit);
public sealed record AuditConsolePage(IReadOnlyList<AuditConsoleRowDto> Items, int Page, int PageSize,
    int TotalCount, int TotalPages, string HighWatermark, bool AuditExportsEnabled);
public interface IWorkspaceAuditConsole
{
    Task<AuditConsolePage> ReadAsync(WorkspaceReadContext context, AuditConsoleOptions options, int page, int size,
        long? highWatermark = null, CancellationToken token = default, bool forExport = false);
}
