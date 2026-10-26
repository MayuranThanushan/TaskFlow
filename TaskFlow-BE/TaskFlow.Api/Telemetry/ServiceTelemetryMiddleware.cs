using System.Diagnostics;

namespace TaskFlow.Api.Telemetry;

public sealed class ServiceTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ServiceTelemetry telemetry)
    {
        using var span = telemetry.StartRequest(context);
        long started = Stopwatch.GetTimestamp();
        bool cancelled = false, unhandled = false;
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { cancelled = true; throw; }
        catch { unhandled = true; throw; }
        finally { cancelled |= context.RequestAborted.IsCancellationRequested; telemetry.CompleteRequest(context, span, Stopwatch.GetElapsedTime(started).TotalSeconds, cancelled, unhandled); }
    }
}
