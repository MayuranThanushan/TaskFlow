using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace TaskFlow.Api.Middleware;

public sealed class RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Trace-Id"] = traceId;
            return Task.CompletedTask;
        });
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["TraceId"] = traceId });
        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            // Route patterns avoid logging IDs, query strings, headers or request bodies.
            var route = (context.GetEndpoint() as RouteEndpoint)
                ?? (context.Features.Get<IExceptionHandlerPathFeature>()?.Endpoint as RouteEndpoint);
            var level = context.Response.StatusCode >= 500 ? LogLevel.Error
                : context.Response.StatusCode >= 400 ? LogLevel.Warning : LogLevel.Information;
            logger.Log(level, new EventId(1000, "http.request_completed"),
                "HTTP {Method} {Route} returned {StatusCode} in {ElapsedMs} ms; trace {TraceId}",
                context.Request.Method,
                route?.RoutePattern.RawText ?? "unmatched",
                context.Response.StatusCode, Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 2), traceId);
        }
    }
}
