using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Workspaces;

public sealed partial class WorkspaceLifecycle
{
    public async Task<WorkspacePolicyDto> ReadPolicyAsync(WorkspaceReadContext context, CancellationToken token = default)
    {
        await using var snapshot = await reads.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await admission.AuthorizeAsync(context, token);
        if (!await reads.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == context.WorkspaceId && m.UserId == context.ActorId
            && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Admin), token)) Deny();
        var workspace = await reads.Workspaces.AsNoTracking().SingleAsync(w => w.Id == context.WorkspaceId, token);
        var projects = reads.Projects.IgnoreQueryFilters().Where(p => p.WorkspaceId == context.WorkspaceId);
        var tasks = reads.Tasks.IgnoreQueryFilters().Where(t => t.WorkspaceId == context.WorkspaceId && t.Project.WorkspaceId == context.WorkspaceId);
        var capacity = new PilotCapacityDto(await projects.CountAsync(p => p.DeletedAtUtc == null, token),
            await tasks.CountAsync(t => t.DeletedAtUtc == null && t.Project.DeletedAtUtc == null, token),
            await reads.WorkspaceMemberships.CountAsync(m => m.WorkspaceId == context.WorkspaceId && m.State == WorkspaceMembershipState.Active, token),
            await projects.CountAsync(token), await tasks.CountAsync(token), PilotQuotas.Projects, PilotQuotas.Tasks, PilotQuotas.Members,
            PilotQuotas.ProjectTasks, PilotQuotas.RetainedProjects, PilotQuotas.RetainedTasks);
        await snapshot.CommitAsync(token);
        return new(Id(workspace.PolicyVersion), workspace.AuditRetentionDays, workspace.DeletedContentRetentionDays, workspace.LegalHold,
            workspace.AuditExportsEnabled, false, capacity);
    }
    public Task ChangePolicyAsync(WorkspaceWriteContext context, WorkspacePolicyChange change, CancellationToken token = default) => Write(context, async (db, actor) =>
    {
        if (actor.Role != WorkspaceRole.Owner) Deny();
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == context.WorkspaceId, token);
        if (!long.TryParse(change.ExpectedVersion, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) || expected <= 0 || Id(expected) != change.ExpectedVersion)
            throw new TaskValidationException("expectedVersion", "Supply the loaded policy revision.");
        if (expected != workspace.PolicyVersion) throw new ResourceVersionException(409, "resource.conflict");
        if (change.AuditRetentionDays is not (90 or 180 or 365 or 730) || change.DeletedContentRetentionDays is not (30 or 90 or 180 or 365))
            throw new TaskValidationException("retention", "Choose a supported retention review period.");
        if (workspace.AuditRetentionDays == change.AuditRetentionDays && workspace.DeletedContentRetentionDays == change.DeletedContentRetentionDays
            && workspace.LegalHold == change.LegalHold && workspace.AuditExportsEnabled == change.AuditExportsEnabled) return;
        await Password(context.ActorId, change.Password, token);
        if (workspace.PolicyVersion == long.MaxValue) throw new ResourceVersionException(409, "resource.version_exhausted");
        workspace.PolicyVersion++;
        workspace.AuditRetentionDays = change.AuditRetentionDays; workspace.DeletedContentRetentionDays = change.DeletedContentRetentionDays;
        workspace.LegalHold = change.LegalHold; workspace.AuditExportsEnabled = change.AuditExportsEnabled;
        Audit(db, context, context.ActorId, "CompanyPolicyChanged");
    }, token);
}
