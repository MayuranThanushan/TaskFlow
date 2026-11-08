using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Identity;

public static class SecurityRecords
{
    public static SecurityEvent Create(SecurityAction action, SecurityOutcome outcome, long? subject, long? actor = null)
    {
        if (!Enum.IsDefined(action) || !Enum.IsDefined(outcome) || subject is <= 0 || actor is <= 0) throw new ArgumentException("Invalid security event.");
        return new()
        {
            SchemaVersion = 1, Action = action.ToString(), Outcome = outcome.ToString(),
            ActorUserId = actor is > 0 ? actor : null, SubjectUserId = subject is > 0 ? subject : null,
            SubjectType = subject is > 0 ? "Account" : "UnknownAccount", CorrelationId = AuditCorrelation.Current()
        };
    }
}
public sealed class SecuritySignalWindow(TimeProvider clock)
{
    private readonly DateTime[] last = new DateTime[1024];
    private readonly object gate = new();
    public bool Admit(long? actor, string route, int status)
    {
        var bucket = (int)((uint)HashCode.Combine(actor ?? 0, route, status) % (uint)last.Length);
        lock (gate)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (last[bucket] != default && now - last[bucket] < TimeSpan.FromMinutes(1)) return false;
            last[bucket] = now; return true;
        }
    }
}
public sealed class SecurityEvents(IServiceScopeFactory scopes, SecuritySignalWindow window, ILogger<SecurityEvents> logger) : ISecurityEvents
{
    public async Task WriteAsync(SecurityAction action, SecurityOutcome outcome, long? subjectId, long? actorId = null, CancellationToken token = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        var record = SecurityRecords.Create(action, outcome, subjectId, actorId);
        db.SecurityEvents.Add(record);
        try { await db.SaveChangesAsync(token); }
        catch (Exception failure)
        {
            logger.LogError(new EventId(3200, "security.persist_failed"), "Security event persistence failed for {Action} ({ExceptionType}); trace {TraceId}", action.ToString(), failure.GetType().Name, record.CorrelationId);
            throw;
        }
    }
    public async Task RejectedRequestAsync(long? actorId, string routeTemplate, int status, CancellationToken token = default)
    {
        if (routeTemplate.Length > 200 || !window.Admit(actorId, routeTemplate, status)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        var record = SecurityRecords.Create(SecurityAction.RequestRejected, SecurityOutcome.Denied, actorId, actorId);
        record.SubjectType = actorId is > 0 ? "Account" : "Anonymous";
        record.RouteTemplate = routeTemplate; record.StatusCode = status; record.Coalesced = true;
        db.SecurityEvents.Add(record); await db.SaveChangesAsync(token);
    }
}
