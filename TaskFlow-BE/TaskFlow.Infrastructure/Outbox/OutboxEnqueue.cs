using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Outbox;

// Structured mutation intent, not activity descriptions, raw Details, recipient data or rendered content.
public static class OutboxEnqueue
{
    public static bool Known(string kind) => kind.StartsWith("Project.", StringComparison.Ordinal)
        ? Enum.TryParse<ActivityAction>(kind[8..], out var action) && Enum.IsDefined(action) && kind == "Project." + action
        : kind is "Company.CompanyCreated" or "Company.CompanyRoleChanged" or "Company.CompanyMemberSuspended"
            or "Company.CompanyMemberReactivated" or "Company.CompanyMemberRemoved" or "Company.CompanyMemberLeft"
            or "Company.CompanyOwnershipTransferred" or "Company.ProjectRoleChanged" or "Company.ProjectOwnershipTransferred"
            or "Company.ProjectMemberAdded" or "Company.ProjectMemberRemoved" or "Company.CompanyPolicyChanged";
    public static void Capture(TaskFlowDbContext db)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Outbox requires the business transaction.");
        foreach (var row in db.ChangeTracker.Entries<ActivityLog>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToArray())
            if (row.WorkspaceId is { } workspace) Add(db, workspace, row.ActorUserId, row.ProjectId, row.TaskId, "Project." + row.ActionType);
        foreach (var row in db.ChangeTracker.Entries<WorkspaceAuditLog>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToArray())
            Add(db, row.WorkspaceId, row.ActorUserId, row.ProjectId, null, "Company." + row.Action);
    }
    private static void Add(TaskFlowDbContext db, long workspace, long actor, long? project, long? task, string kind)
    {
        if (!Known(kind)) throw new InvalidOperationException("Unregistered business event kind.");
        var message = new OutboxEvent { WorkspaceId = workspace, ActorUserId = actor, ProjectId = project, TaskId = task,
            Kind = kind, CorrelationId = AuditCorrelation.Current(), CreatedAtUtc = DateTime.UtcNow };
        db.OutboxDeliveries.Add(new OutboxDelivery { WorkspaceId = workspace, Event = message });
    }
}
