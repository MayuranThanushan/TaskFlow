namespace TaskFlow.Domain.Entities;

public interface IVersionedAudit
{
    int SchemaVersion { get; set; }
    string? Outcome { get; set; }
    string? CorrelationId { get; set; }
    string? SubjectType { get; set; }
    string? SubjectId { get; set; }
    string? ChangesJson { get; set; }
}

public sealed class SecurityEvent
{
    public long Id { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string Action { get; set; } = "";
    public string Outcome { get; set; } = "";
    public long? ActorUserId { get; set; }
    public long? SubjectUserId { get; set; }
    public string SubjectType { get; set; } = "Account";
    public string CorrelationId { get; set; } = "";
    public string? RouteTemplate { get; set; }
    public int? StatusCode { get; set; }
    public bool Coalesced { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
