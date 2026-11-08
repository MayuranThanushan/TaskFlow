namespace TaskFlow.Domain.Entities;
// Global account security mail is separate from tenant checkpoint facts.
public sealed class AccountEmailJob
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public string Kind { get; set; } = "";
    public string? ProtectedPayload { get; set; }
    public string State { get; set; } = "Pending";
    public int Attempts { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public string? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? LastCode { get; set; }
}
