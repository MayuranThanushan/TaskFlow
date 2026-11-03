using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Outbox;

public sealed record OutboxMessage(long Id, string MessageId, long WorkspaceId, long ActorUserId, long? ProjectId, long? TaskId, string Kind, int SchemaVersion, string CorrelationId);
public sealed record DeliveryLease(long DeliveryId, long WorkspaceId, string Token, int Attempt, OutboxMessage Message);
public sealed record OutboxCounts(int Pending, int Leased, int DeadLetter, double OldestAgeSeconds);
public sealed class OutboxStore(TaskFlowDbContext db)
{
    public const int MaxAttempts = 5, MaxReplays = 3, LeaseSeconds = 60;
    private void Reset()
    {
        if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Worker store requires an isolated clean context.");
        db.ChangeTracker.Clear();
    }
    private static async Task<DateTime> Now(TaskFlowDbContext database, CancellationToken token)
    {
        await using var command = database.Database.GetDbConnection().CreateCommand();
        command.Transaction = database.Database.CurrentTransaction!.GetDbTransaction(); command.CommandText = "SELECT UTC_TIMESTAMP(6)";
        return DateTime.SpecifyKind(Convert.ToDateTime(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
    private void Record(OutboxDelivery work, AttemptOutcome outcome, DateTime now) => db.OutboxAttempts.Add(new OutboxAttempt
        { WorkspaceId = work.WorkspaceId, DeliveryId = work.Id, Attempt = work.Attempts, Outcome = outcome, FailureCode = outcome == AttemptOutcome.LeaseExpired ? "lease.expired" : outcome is AttemptOutcome.Retry or AttemptOutcome.DeadLetter or AttemptOutcome.Replayed ? work.LastFailureCode : null, CreatedAtUtc = now });
    public async Task<DeliveryLease?> ClaimAsync(CancellationToken token = default)
    {
        Reset();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); var now = await Now(db, token);
        // MySQL SKIP LOCKED coordinates independent processes; no lock is held during consumption.
        var rows = await db.OutboxDeliveries.FromSqlInterpolated($"SELECT * FROM `OutboxDeliveries` WHERE `Consumer`='checkpoint.v1' AND ((`State`='Pending' AND `AvailableAtUtc` <= {now}) OR (`State`='Leased' AND `LeaseExpiresAtUtc` <= {now})) ORDER BY `AvailableAtUtc`,`Id` LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(token);
        var work = rows.SingleOrDefault(); if (work is null) { await transaction.CommitAsync(token); return null; }
        now = await Now(db, token); // Start TTL after obtaining the row lock, not before a possible wait.
        if (work.State == DeliveryState.Leased) Record(work, AttemptOutcome.LeaseExpired, now);
        if (work.RoundAttempts >= MaxAttempts)
        {
            work.State = DeliveryState.DeadLetter; work.LastFailureCode = "lease.exhausted"; work.LeaseToken = null; work.LeaseExpiresAtUtc = null;
            Record(work, AttemptOutcome.DeadLetter, now); await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return null;
        }
        work.Attempts++; work.RoundAttempts++; work.State = DeliveryState.Leased; work.LeaseToken = Guid.NewGuid().ToString("N"); work.LeaseExpiresAtUtc = now.AddSeconds(LeaseSeconds);
        Record(work, AttemptOutcome.Started, now);
        var message = await db.OutboxEvents.AsNoTracking().SingleAsync(e => e.Id == work.EventId && e.WorkspaceId == work.WorkspaceId, token);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
        return new(work.Id, work.WorkspaceId, work.LeaseToken, work.Attempts, new(message.Id, message.MessageId, message.WorkspaceId, message.ActorUserId,
            message.ProjectId, message.TaskId, message.Kind, message.SchemaVersion, message.CorrelationId));
    }
    public async Task<bool> CompleteAsync(DeliveryLease lease, bool success, bool permanent = false, CancellationToken token = default)
    {
        Reset();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token);
        var rows = await db.OutboxDeliveries.FromSqlInterpolated($"SELECT * FROM `OutboxDeliveries` WHERE `Id`={lease.DeliveryId} AND `WorkspaceId`={lease.WorkspaceId} FOR UPDATE").ToListAsync(token);
        var now = await Now(db, token); // Recheck expiry after acquiring the row lock.
        var work = rows.SingleOrDefault();
        if (work is null || work.Consumer != "checkpoint.v1" || work.State != DeliveryState.Leased || work.LeaseToken != lease.Token || work.LeaseExpiresAtUtc is null || work.LeaseExpiresAtUtc <= now)
        { await transaction.CommitAsync(token); return false; }
        work.LeaseToken = null; work.LeaseExpiresAtUtc = null;
        if (success) { work.State = DeliveryState.Succeeded; work.CompletedAtUtc = now; work.LastFailureCode = null; Record(work, AttemptOutcome.Succeeded, now); }
        else if (permanent || work.RoundAttempts >= MaxAttempts)
        { work.State = DeliveryState.DeadLetter; work.LastFailureCode = permanent ? "message.unsupported" : "consumer.exhausted"; Record(work, AttemptOutcome.DeadLetter, now); }
        else
        { work.State = DeliveryState.Pending; work.LastFailureCode = "consumer.transient"; work.AvailableAtUtc = now.AddSeconds(Math.Min(300, 5 * (1 << (work.RoundAttempts - 1)))); Record(work, AttemptOutcome.Retry, now); }
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return true;
    }
    public async Task<bool> ReplayAsync(long workspace, long delivery, CancellationToken token = default)
    {
        Reset();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); var now = await Now(db, token);
        var rows = await db.OutboxDeliveries.FromSqlInterpolated($"SELECT * FROM `OutboxDeliveries` WHERE `Id`={delivery} AND `WorkspaceId`={workspace} FOR UPDATE").ToListAsync(token);
        var work = rows.SingleOrDefault();
        if (work is null || work.Consumer != "checkpoint.v1" || work.State != DeliveryState.DeadLetter || work.Replays >= MaxReplays) { await transaction.CommitAsync(token); return false; }
        work.Replays++; work.RoundAttempts = 0; work.State = DeliveryState.Pending; work.AvailableAtUtc = now;
        // Replay is a separate immutable transition; attempt number stays monotonic.
        Record(work, AttemptOutcome.Replayed, now); work.LastFailureCode = null; await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return true;
    }
    public async Task<OutboxCounts> CountsAsync(CancellationToken token = default)
    {
        Reset();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token); var now = await Now(db, token);
        var own = db.OutboxDeliveries.Where(d => d.Consumer == "checkpoint.v1");
        var pending = await own.CountAsync(d => d.State == DeliveryState.Pending, token);
        var leased = await own.CountAsync(d => d.State == DeliveryState.Leased, token);
        var dead = await own.CountAsync(d => d.State == DeliveryState.DeadLetter, token);
        var oldest = await own.Where(d => d.State == DeliveryState.Pending || d.State == DeliveryState.Leased).Select(d => (DateTime?)d.Event.CreatedAtUtc).MinAsync(token);
        await transaction.CommitAsync(token); return new(pending, leased, dead, oldest is null ? 0 : Math.Max(0, (now - oldest.Value).TotalSeconds));
    }
}
