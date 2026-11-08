namespace TaskFlow.Domain.Entities;

// Global account credential material. Never part of company audit, outbox or API readers.
public sealed class AccountMfa
{
    public long UserId { get; set; }
    public string? ProtectedSecret { get; set; }
    public string? PendingProtectedSecret { get; set; }
    public DateTime? PendingExpiresAtUtc { get; set; }
    public long LastAcceptedStep { get; set; } = -1;
    public string RecoveryHashes { get; set; } = "[]";
}
