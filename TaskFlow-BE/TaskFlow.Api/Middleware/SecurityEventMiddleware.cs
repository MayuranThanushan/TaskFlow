using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using TaskFlow.Application.Features.Activity;

namespace TaskFlow.Api.Middleware;

public sealed class SecurityEventMiddleware(RequestDelegate next, ILogger<SecurityEventMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ISecurityEvents events)
    {
        await next(context);
        var endpoint = (context.GetEndpoint() as RouteEndpoint) ?? context.Features.Get<IExceptionHandlerPathFeature>()?.Endpoint as RouteEndpoint;
        var route = endpoint?.RoutePattern.RawText;
        if (route is null || !route.TrimStart('/').StartsWith("api/", StringComparison.Ordinal)) return;
        var status = context.Response.StatusCode;
        var rejected = status is 401 or 403 or 404 or 429 || status == 409 && route.TrimStart('/').StartsWith("api/v2/workspaces/", StringComparison.Ordinal) || status == 400
            && (context.Items.ContainsKey("security.csrf_rejected") || route.TrimStart('/').StartsWith("api/v1/auth/", StringComparison.Ordinal));
        if (!rejected) return;
        long? actor = long.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id > 0 ? id : null;
        try { await events.RejectedRequestAsync(actor, route, status, CancellationToken.None); }
        catch (Exception failure)
        {
            // A denied response remains denied even if its coalesced diagnostic sink is unavailable.
            logger.LogError(new EventId(3201, "security.sink_unavailable"), "Security signal persistence failed ({ExceptionType}); trace {TraceId}", failure.GetType().Name, context.TraceIdentifier);
        }
    }
}
