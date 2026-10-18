using System.Diagnostics;

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
            logger.LogInformation("HTTP {Method} {Route} returned {StatusCode} in {ElapsedMs} ms",
                context.Request.Method,
                (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched",
                context.Response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }
}
