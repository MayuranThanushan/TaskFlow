using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Outbox;

public sealed record DeliveryQueueHealth(string Queue, long Pending, long Due, long DeadLetter, long ExpiredLeases, long Retrying, double OldestDueAgeSeconds);
public sealed record DeliveryHealthSnapshot(int Version, DateTime ObservedAtUtc, DeliveryQueueHealth[] Queues);

// Explicit private operations command only. Reads aggregate state columns, never payloads,
// recipients, resource titles, tokens or per-company identities. Does not claim delivery.
public static class DeliveryHealth
{
    public static async Task<DeliveryHealthSnapshot> ReadAsync(TaskFlowDbContext db, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20)); token = deadline.Token;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = transaction.GetDbTransaction(); command.CommandText = "SELECT UTC_TIMESTAMP(6)";
        var now = DateTime.SpecifyKind(Convert.ToDateTime(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var queues = new List<DeliveryQueueHealth>();
        // Closed identifiers only; no user-provided SQL or historical content reads.
        foreach (var (name, table, state, extra, leased) in new[] {
            ("checkpoint", "OutboxDeliveries", "State", "1=1", true),
            ("account_mail", "AccountEmailJobs", "State", "1=1", true),
            ("invitation_mail", "WorkspaceInvitations", "DeliveryState", "State='Pending'", true),
            ("watcher", "WatcherNotificationIntents", "State", "1=1", false) })
        {
            var pending = $"{state} IN ('Pending','Leased')";
            var due = $"({state}='Pending' AND AvailableAtUtc<=@now)";
            var expired = leased ? $"({state}='Leased' AND LeaseExpiresAtUtc<=@now)" : "FALSE";
            var oldest = leased ? $"CASE WHEN {due} THEN AvailableAtUtc WHEN {expired} THEN LeaseExpiresAtUtc END" : $"CASE WHEN {due} THEN AvailableAtUtc END";
            command.CommandText = $"SELECT COALESCE(SUM({pending}),0), COALESCE(SUM({due} OR {expired}),0), COALESCE(SUM({state}='DeadLetter'),0), COALESCE(SUM({expired}),0), COALESCE(SUM({(leased ? pending + " AND Attempts>1" : "FALSE")}),0), MIN({oldest}) FROM {table} WHERE {extra}";
            command.Parameters.Clear(); var parameter = command.CreateParameter(); parameter.ParameterName = "@now"; parameter.Value = now; command.Parameters.Add(parameter);
            await using var reader = await command.ExecuteReaderAsync(token); await reader.ReadAsync(token);
            queues.Add(new(name, reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.IsDBNull(5) ? 0 : Math.Max(0, (now-reader.GetDateTime(5)).TotalSeconds)));
        }
        await transaction.CommitAsync(token); return new(1, now, queues.ToArray());
    }
}
