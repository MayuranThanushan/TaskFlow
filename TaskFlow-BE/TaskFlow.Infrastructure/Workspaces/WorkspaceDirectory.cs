using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Workspaces;

public sealed class WorkspaceDirectory(TaskFlowDbContext database) : IWorkspaceDirectory
{
    public async Task<WorkspaceDto> CreateAsync(long actorId, string name, CancellationToken token = default)
    {
        if (actorId <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        name = TextRules.Required(name, 150, nameof(name));
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        // A seeded singleton row serializes fleet/company-owner admission across processes.
        var gate = await database.PilotGates.FromSqlRaw("SELECT * FROM `PilotGates` WHERE `Id` = 1 FOR UPDATE").ToListAsync(token);
        if (gate.Count != 1) throw new InvalidOperationException("Pilot admission gate is unavailable.");
        if (await database.Workspaces.CountAsync(token) >= PilotQuotas.Companies
            || await database.WorkspaceMemberships.CountAsync(m => m.UserId == actorId && m.Role == WorkspaceRole.Owner && m.State == WorkspaceMembershipState.Active, token) >= PilotQuotas.OwnedCompanies)
            PilotQuotas.Deny();
        var workspace = new Workspace { Name = name };
        database.Workspaces.Add(workspace);
        await database.SaveChangesAsync(token);
        database.WorkspaceMemberships.Add(new WorkspaceMembership
        {
            WorkspaceId = workspace.Id, UserId = actorId,
            Role = WorkspaceRole.Owner, State = WorkspaceMembershipState.Active
        });
        await database.SaveChangesAsync(token);
        database.WorkspaceAuditLogs.Add(new WorkspaceAuditLog { WorkspaceId = workspace.Id, ActorUserId = actorId, TargetUserId = actorId, Action = "CompanyCreated", ToRole = "Owner" });
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return new(workspace.Id.ToString(CultureInfo.InvariantCulture), workspace.Name, WorkspaceRole.Owner.ToString());
    }
}
