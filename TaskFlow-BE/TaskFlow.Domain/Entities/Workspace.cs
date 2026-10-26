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
public enum WorkspaceMembershipState { Active, Suspended }
