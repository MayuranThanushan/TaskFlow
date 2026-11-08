namespace TaskFlow.Application.Features.Workspaces;

public sealed class PilotQuotaException : Exception { }
public sealed record PilotCapacityDto(int ActiveProjects, int ActiveTasks, int ActiveMembers, int RetainedProjects, int RetainedTasks,
    int ProjectLimit, int TaskLimit, int MemberLimit, int TasksPerProjectLimit, int RetainedProjectLimit, int RetainedTaskLimit, int ReservedMemberSeats);
public sealed record WorkspacePolicyDto(string Version, int AuditRetentionDays, int DeletedContentRetentionDays, bool LegalHold,
    bool AuditExportsEnabled, bool AutomaticPurgeEnabled, PilotCapacityDto Capacity);
public sealed record WorkspacePolicyChange(string ExpectedVersion, int AuditRetentionDays, int DeletedContentRetentionDays,
    bool LegalHold, bool AuditExportsEnabled, string Password);
public interface IWorkspacePolicy
{
    Task<WorkspacePolicyDto> ReadPolicyAsync(WorkspaceReadContext context, CancellationToken token = default);
    Task ChangePolicyAsync(WorkspaceWriteContext context, WorkspacePolicyChange change, CancellationToken token = default);
}
