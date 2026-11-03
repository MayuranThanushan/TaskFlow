namespace TaskFlow.Domain.Entities;

public enum DeliveryState { Pending, Leased, Succeeded, DeadLetter }
public enum AttemptOutcome { Started, Succeeded, Retry, DeadLetter, LeaseExpired, Replayed }
public sealed class OutboxEvent
{
    public long Id { get; set; }
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");
    public long WorkspaceId { get; set; }
    public long ActorUserId { get; set; }
    public long? ProjectId { get; set; }
    public long? TaskId { get; set; }
    public int SchemaVersion { get; set; } = 1;
    public string Kind { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
}
public sealed class OutboxDelivery
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long EventId { get; set; }
    public OutboxEvent Event { get; set; } = null!;
    public string Consumer { get; set; } = "checkpoint.v1";
    public DeliveryState State { get; set; }
    public int Attempts { get; set; }
    public int RoundAttempts { get; set; }
    public int Replays { get; set; }
    public DateTime AvailableAtUtc { get; set; }
    public string? LeaseToken { get; set; }
    public DateTime? LeaseExpiresAtUtc { get; set; }
    public string? LastFailureCode { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}
public sealed class OutboxAttempt
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long DeliveryId { get; set; }
    public int Attempt { get; set; }
    public AttemptOutcome Outcome { get; set; }
    public string? FailureCode { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}
