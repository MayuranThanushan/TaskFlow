namespace TaskFlow.Domain.Entities;
public sealed class WorkspaceInvitation
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long InviterUserId { get; set; }
    public long? ProjectId { get; set; }
    public string Email { get; set; } = "";
    public string NormalizedEmail { get; set; } = "";
    public string? PendingEmail { get; set; }
    public string Role { get; set; } = "";
    public string State { get; set; } = "Pending";
    public long Version { get; set; } = 1;
    public string? TokenHash { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime LastSentAtUtc { get; set; }
    public int Resends { get; set; }
    public long? AcceptedUserId { get; set; }
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string? ProtectedMessage { get; set; }
    public string DeliveryState { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public string? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? LastCode { get; set; }
}
public sealed class InvitationTransition
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long InvitationId { get; set; }
    public long ActorUserId { get; set; }
    public long Version { get; set; }
    public string Action { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}
