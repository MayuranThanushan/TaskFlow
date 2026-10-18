using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Api.Health;

public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
            if (!await database.Database.CanConnectAsync(cancellationToken)) return HealthCheckResult.Unhealthy();
            var compiled = database.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = (await database.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            // An older binary must not declare an unrecognized future schema ready.
            if (!compiled.SetEquals(applied)) return HealthCheckResult.Unhealthy();
            // Verify a mapped table is readable, without changing schema or user data.
            await database.Users.AsNoTracking().Select(user => user.Id).Take(1).ToListAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Health responses and logs must not expose connection strings or database diagnostics.
            return HealthCheckResult.Unhealthy();
        }
    }
}
