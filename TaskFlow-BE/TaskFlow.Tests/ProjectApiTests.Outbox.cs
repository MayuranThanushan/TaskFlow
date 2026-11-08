using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Outbox;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact]
    public async Task OutboxIntentCommitsWithMutationAndContainsOnlyScopedIdentifiers()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var response = await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/tasks", new { title = "PRIVATE_OUTBOX_CANARY", description = "PRIVATE_BODY_CANARY" }); Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = fixture.CreateContext(); var message = await db.OutboxEvents.SingleAsync(e => e.WorkspaceId == c.A.Id);
        Assert.Equal("Project.TaskCreated", message.Kind); Assert.Equal(c.A1.Id, message.ProjectId); Assert.NotNull(message.TaskId); Assert.Equal(host.Users[0].Id, message.ActorUserId);
        Assert.Equal(response.Headers.GetValues("X-Trace-Id").Single(), message.CorrelationId); Assert.Equal(32, message.MessageId.Length);
        Assert.DoesNotContain("CANARY", JsonSerializer.Serialize(message)); Assert.DoesNotContain(host.Users[0].Email!, JsonSerializer.Serialize(message));
        Assert.Equal(DeliveryState.Pending, (await db.OutboxDeliveries.SingleAsync(d => d.EventId == message.Id)).State);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.B) + "/projects/" + c.A1.Id + "/tasks", new { title = "Denied" }), HttpStatusCode.NotFound);
        Assert.Equal(1, await db.OutboxEvents.CountAsync(e => e.WorkspaceId == c.A.Id));
        foreach (var path in new[] { W(c.A) + "/outbox", "/api/v1/outbox", W(c.A) + "/deliveries" }) await Problem(await owner.GetAsync(path), HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task OutboxFailureRollsBackBusinessAndSuccessAudit()
    {
        await using var host = new ProjectHost(fixture, true, true, true, new FailOutbox()); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/tasks", new { title = "Rollback outbox" }), HttpStatusCode.InternalServerError);
        await using var db = fixture.CreateContext(); Assert.False(await db.Tasks.AnyAsync(t => t.Title == "Rollback outbox")); Assert.False(await db.ActivityLogs.AnyAsync(a => a.WorkspaceId == c.A.Id && a.ActionType == ActivityAction.TaskCreated && a.TaskId != c.TaskA.Id)); Assert.False(await db.OutboxEvents.AnyAsync(e => e.WorkspaceId == c.A.Id));
    }
    [MySqlFact]
    public async Task CompetingWorkersFenceExpiredLeasesAndAcknowledgeExactlyOneInternalCheckpoint()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "One work item" });
        await using var a = fixture.CreateContext(); await using var b = fixture.CreateContext(); var first = new OutboxStore(a); var second = new OutboxStore(b);
        var claims = await Task.WhenAll(first.ClaimAsync(), second.ClaimAsync()); var lease = Assert.Single(claims, l => l is not null)!;
        Assert.False(await first.CompleteAsync(lease with { WorkspaceId = c.B.Id }, true));
        await using (var sql = fixture.CreateContext()) await sql.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutboxDeliveries SET LeaseExpiresAtUtc=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE Id={lease.DeliveryId}");
        var replacement = (await second.ClaimAsync())!; Assert.NotNull(replacement); Assert.NotEqual(lease.Token, replacement.Token);
        Assert.False(await first.CompleteAsync(lease, true)); Assert.True(await second.CompleteAsync(replacement, true)); Assert.False(await first.CompleteAsync(replacement, true));
        await using var verify = fixture.CreateContext(); Assert.Equal(DeliveryState.Succeeded, (await verify.OutboxDeliveries.SingleAsync(d => d.Id == lease.DeliveryId)).State);
        Assert.Equal(1, await verify.OutboxAttempts.CountAsync(x => x.DeliveryId == lease.DeliveryId && x.Outcome == AttemptOutcome.Succeeded)); Assert.Equal(1, await verify.OutboxAttempts.CountAsync(x => x.DeliveryId == lease.DeliveryId && x.Outcome == AttemptOutcome.LeaseExpired));
    }
    [MySqlFact]
    public async Task RetryBackoffDeadLetterAndBoundedReplayKeepTheSameMessageIdentity()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "Retry work" });
        await using var db = fixture.CreateContext(); var store = new OutboxStore(db); DeliveryLease? lease = null;
        for (int i = 0; i < OutboxStore.MaxAttempts; i++)
        {
            lease = await store.ClaimAsync(); Assert.NotNull(lease); Assert.True(await store.CompleteAsync(lease, false));
            if (i < OutboxStore.MaxAttempts - 1) { Assert.Null(await store.ClaimAsync()); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutboxDeliveries SET AvailableAtUtc=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE Id={lease.DeliveryId}"); }
        }
        Assert.Equal(1, (await store.CountsAsync()).DeadLetter);
        Assert.False(await store.ReplayAsync(c.B.Id, lease!.DeliveryId));
        for (int i = 0; i < OutboxStore.MaxReplays; i++)
        {
            Assert.True(await store.ReplayAsync(c.A.Id, lease.DeliveryId)); Assert.False(await store.ReplayAsync(c.A.Id, lease.DeliveryId));
            var next = (await store.ClaimAsync())!; Assert.Equal(lease.Message.MessageId, next.Message.MessageId); Assert.True(await store.CompleteAsync(next, false, permanent: true));
        }
        Assert.False(await store.ReplayAsync(c.A.Id, lease.DeliveryId));
        Assert.Equal(1, await db.OutboxEvents.CountAsync(e => e.WorkspaceId == c.A.Id)); Assert.Equal(1, await db.OutboxDeliveries.CountAsync(d => d.WorkspaceId == c.A.Id));
    }
    [MySqlFact]
    public async Task UnknownFormatsDeadLetterWithoutRenderingAndHistoryRemainsAppendOnly()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "Unsupported work" });
        await using var db = fixture.CreateContext(); var message = await db.OutboxEvents.SingleAsync(e => e.WorkspaceId == c.A.Id);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutboxEvents SET SchemaVersion=99 WHERE Id={message.Id}");
        var store = new OutboxStore(db); var lease = (await store.ClaimAsync())!; Assert.False(await new CheckpointConsumer().ConsumeAsync(lease.Message, CancellationToken.None)); Assert.True(await store.CompleteAsync(lease, false, true));
        var record = await db.OutboxAttempts.FirstAsync(a => a.WorkspaceId == c.A.Id); record.Attempt = 99; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear(); var immutable = await db.OutboxEvents.SingleAsync(e => e.Id == message.Id); immutable.Kind = "Tampered"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task OutboxCompositeReferencesRejectCrossCompanyMetadata()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "Parent event" });
        await using var db = fixture.CreateContext(); var source = await db.OutboxEvents.SingleAsync(e => e.WorkspaceId == c.A.Id);
        db.OutboxEvents.Add(new OutboxEvent { WorkspaceId = c.A.Id, ActorUserId = host.Users[0].Id, ProjectId = c.B1.Id, Kind = "Project.ProjectUpdated", CorrelationId = "server", CreatedAtUtc = DateTime.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        db.OutboxDeliveries.Add(new OutboxDelivery { WorkspaceId = c.B.Id, EventId = source.Id, Consumer = "foreign" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        Assert.Equal(1, await db.OutboxEvents.CountAsync(e => e.WorkspaceId == c.A.Id)); Assert.False(await db.OutboxDeliveries.AnyAsync(d => d.Consumer == "foreign"));
    }
    [MySqlFact]
    public async Task PumpRetriesThrownConsumersWithoutPersistingExceptionTextAndContinuesAfterRestart()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "Pump work" });
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddSingleton<TimeProvider>(TimeProvider.System); services.AddDbContext<TaskFlow.Infrastructure.Persistence.TaskFlowDbContext>(o => o.UseMySQL(fixture.ConnectionString));
        services.AddScoped<OutboxStore>(); using var provider = services.BuildServiceProvider(); using var metrics = new OutboxMetrics();
        var failed = new OutboxPump(provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), new ThrowingConsumer(), metrics);
        Assert.Equal(1, await failed.RunBatchAsync());
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal("consumer.transient", (await db.OutboxDeliveries.SingleAsync(d => d.WorkspaceId == c.A.Id)).LastFailureCode);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutboxDeliveries SET AvailableAtUtc=UTC_TIMESTAMP(6)-INTERVAL 1 SECOND WHERE WorkspaceId={c.A.Id}");
        }
        var restarted = new OutboxPump(provider.GetRequiredService<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>(), new CheckpointConsumer(), metrics);
        Assert.Equal(1, await restarted.RunBatchAsync());
        await using var verify = fixture.CreateContext(); Assert.Equal(DeliveryState.Succeeded, (await verify.OutboxDeliveries.SingleAsync(d => d.WorkspaceId == c.A.Id)).State);
        Assert.Equal(1, await verify.OutboxAttempts.CountAsync(a => a.WorkspaceId == c.A.Id && a.Outcome == AttemptOutcome.Retry && a.FailureCode == "consumer.transient"));
        Assert.DoesNotContain("PRIVATE_PROVIDER_CANARY", JsonSerializer.Serialize(await verify.OutboxAttempts.Where(a => a.WorkspaceId == c.A.Id).ToArrayAsync()));
    }

    [MySqlFact]
    public async Task AcknowledgementChecksDatabaseTimeAfterWaitingForTheDeliveryLock()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "Clock fence" });
        await using var initial = fixture.CreateContext(); var lease = (await new OutboxStore(initial).ClaimAsync())!;
        await using var blocker = fixture.CreateContext(); await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.OutboxDeliveries.FromSqlInterpolated($"SELECT * FROM OutboxDeliveries WHERE Id={lease.DeliveryId} FOR UPDATE").ToListAsync();
        var signal = new AckLockSignal();
        var options = new DbContextOptionsBuilder<TaskFlow.Infrastructure.Persistence.TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(signal).Options;
        await using var ackContext = new TaskFlow.Infrastructure.Persistence.TaskFlowDbContext(options, TimeProvider.System);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)); var acknowledge = new OutboxStore(ackContext).CompleteAsync(lease, true, token: deadline.Token);
        await signal.Reading.Task.WaitAsync(deadline.Token);
        // Expiry is later than a pre-lock clock sample but earlier than lock release. No sleep or clock override.
        await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE OutboxDeliveries SET LeaseExpiresAtUtc=UTC_TIMESTAMP(6) WHERE Id={lease.DeliveryId}");
        await transaction.CommitAsync(); Assert.False(await acknowledge);
    }
    private sealed class AckLockSignal : DbCommandInterceptor
    {
        public TaskCompletionSource Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(System.Data.Common.DbCommand command, CommandEventData data, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken token = default)
        { if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) Reading.TrySetResult(); return base.ReaderExecutingAsync(command, data, result, token); }
    }
    private sealed class ThrowingConsumer : IOutboxConsumer
    {
        public Task<bool> ConsumeAsync(OutboxMessage message, CancellationToken token) => throw new InvalidOperationException("PRIVATE_PROVIDER_CANARY");
    }
    private sealed class FailOutbox : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (data.Context!.ChangeTracker.Entries<OutboxEvent>().Any(e => e.State == EntityState.Added)) throw new InvalidOperationException("Synthetic enqueue failure"); return base.SavingChangesAsync(data, result, cancellationToken); }
    }
}
