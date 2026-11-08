using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
namespace TaskFlow.Infrastructure.Outbox;

public interface IOutboxConsumer { Task<bool> ConsumeAsync(OutboxMessage message, CancellationToken token); }
// Internal metadata checkpoint only. Success never means email/user notification delivery.
public sealed class CheckpointConsumer : IOutboxConsumer
{
    public Task<bool> ConsumeAsync(OutboxMessage message, CancellationToken token) => Task.FromResult(message.SchemaVersion == 1 && OutboxEnqueue.Known(message.Kind));
}
public sealed class OutboxMetrics : IDisposable
{
    private readonly Meter meter = new("TaskFlow.Outbox", "1.0");
    private readonly Counter<long> attempts;
    private OutboxCounts? latest;
    private int ready;
    public OutboxMetrics()
    {
        meter.CreateObservableGauge("taskflow.worker.ready", () => Volatile.Read(ref ready), "{state}");
        attempts = meter.CreateCounter<long>("taskflow.worker.delivery.attempts", "{attempt}");
        meter.CreateObservableGauge("taskflow.worker.outbox.pending", () => Volatile.Read(ref latest) is { } s ? new[] { new Measurement<int>(s.Pending + s.Leased) } : [], "{message}");
        meter.CreateObservableGauge("taskflow.worker.outbox.dead_letter", () => Volatile.Read(ref latest) is { } s ? new[] { new Measurement<int>(s.DeadLetter) } : [], "{message}");
        meter.CreateObservableGauge("taskflow.worker.outbox.oldest.age", () => Volatile.Read(ref latest) is { } s ? new[] { new Measurement<double>(s.OldestAgeSeconds) } : [], "s");
    }
    public void Observed(OutboxCounts value) { Volatile.Write(ref latest, value); Volatile.Write(ref ready, 1); }
    public void Unavailable() => Volatile.Write(ref ready, 0);
    public void Attempt() => attempts.Add(1);
    public void Dispose() => meter.Dispose();
}
public sealed class OutboxPump(IServiceScopeFactory scopes, IOutboxConsumer consumer, OutboxMetrics metrics)
{
    public async Task<int> RunBatchAsync(int maximum = 25, CancellationToken token = default)
    {
        if (maximum is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(maximum));
        int completed = 0;
        for (int i = 0; i < maximum; i++)
        {
            DeliveryLease? lease;
            await using (var scope = scopes.CreateAsyncScope()) lease = await scope.ServiceProvider.GetRequiredService<OutboxStore>().ClaimAsync(token);
            if (lease is null) break;
            metrics.Attempt(); bool success = false, permanent = false;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
            try { success = await consumer.ConsumeAsync(lease.Message, deadline.Token).WaitAsync(deadline.Token); permanent = !success; }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { /* No payload, provider message or exception text enters persisted failures. */ }
            await using var finalScope = scopes.CreateAsyncScope();
            if (await finalScope.ServiceProvider.GetRequiredService<OutboxStore>().CompleteAsync(lease, success, permanent, token)) completed++;
        }
        await using var sample = scopes.CreateAsyncScope(); metrics.Observed(await sample.ServiceProvider.GetRequiredService<OutboxStore>().CountsAsync(token));
        return completed;
    }
}
