using System.Diagnostics;
using TaskFlow.Api.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Api.Health;

public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopeFactory, ILogger<DatabaseReadinessCheck> logger, ServiceTelemetry telemetry) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var span = telemetry.Traces.StartActivity("database.readiness");
        if (span is not null) span.TraceStateString = null;
        string result = "cancelled";
        bool? ready = null;
        HealthCheckResult NotReady(string code) { result = code; ready = false; return Failure(code); }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
            if (!await database.Database.CanConnectAsync(cancellationToken)) return NotReady("database.unreachable");
            var compiled = database.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = (await database.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            // An older binary must not declare an unrecognized future schema ready.
            if (!compiled.SetEquals(applied)) return NotReady("database.schema_mismatch");
            // Verify a mapped table is readable, without changing schema or user data.
            await database.Users.AsNoTracking().Select(user => user.Id).Take(1).ToListAsync(cancellationToken);
            result = "ready"; ready = true;
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            // Health responses and logs must not expose connection strings or database diagnostics.
            return NotReady(error is PersistenceConfigurationException ? "database.configuration_missing" : "database.check_failed");
        }
        finally
        {
            telemetry.Readiness(ready, result, Stopwatch.GetElapsedTime(started).TotalSeconds);
            span?.SetTag("result", result);
            if (ready != true) span?.SetStatus(ActivityStatusCode.Error);
        }
    }

    private HealthCheckResult Failure(string code)
    {
        logger.LogWarning(new EventId(2002, "database.not_ready"), "Database readiness failed: {FailureCode}", code);
        return HealthCheckResult.Unhealthy();
    }
}
