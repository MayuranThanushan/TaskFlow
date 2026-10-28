namespace TaskFlow.Domain.Entities;

public sealed class Workspace : AuditableEntity
{
    public string Name { get; set; } = "";
    // Unique, persisted legacy mapping; null for future explicitly created companies.
    public long? LegacyOwnerId { get; set; }
}

public sealed class WorkspaceMembership
{
    public long WorkspaceId { get; set; }
    public long UserId { get; set; }
    public WorkspaceRole Role { get; set; }
    public WorkspaceMembershipState State { get; set; } = WorkspaceMembershipState.Active;
    public DateTime JoinedAtUtc { get; set; }
}

public enum WorkspaceRole { Owner, Admin, Member, Guest }
public enum WorkspaceMembershipState { Active, Suspended, Removed }

public sealed class WorkspaceAuditLog
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long ActorUserId { get; set; }
    public long TargetUserId { get; set; }
    public long? ProjectId { get; set; }
    public string Action { get; set; } = "";
    public string? FromRole { get; set; }
    public string? ToRole { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
