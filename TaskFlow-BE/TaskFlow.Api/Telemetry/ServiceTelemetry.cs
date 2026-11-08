using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace TaskFlow.Api.Telemetry;

public sealed class ServiceTelemetry : IDisposable
{
    public const string SourceName = "TaskFlow.Api";
    public Meter Meter { get; } = new(SourceName, "1.0.0");
    public ActivitySource Traces { get; } = new(SourceName, "1.0.0");
    private readonly Counter<long> requests, errors, readinessChecks, security;
    private readonly Histogram<double> requestDuration, readinessDuration;
    private int readiness = -1;
    private long lastCompletedProbe;

    public ServiceTelemetry()
    {
        requests = Meter.CreateCounter<long>("taskflow.http.requests", "{request}");
        errors = Meter.CreateCounter<long>("taskflow.http.errors", "{request}");
        requestDuration = Duration("taskflow.http.request.duration");
        readinessChecks = Meter.CreateCounter<long>("taskflow.database.readiness.checks", "{check}");
        readinessDuration = Duration("taskflow.database.readiness.duration");
        Meter.CreateObservableGauge("taskflow.database.readiness", () => Volatile.Read(ref readiness), "{state}", "Last completed probe: -1 unknown, 0 not ready, 1 ready. Not a continuous database monitor.");
        Meter.CreateObservableGauge("taskflow.database.readiness.age", () => Interlocked.Read(ref lastCompletedProbe) is var stamp && stamp != 0 ? Stopwatch.GetElapsedTime(stamp).TotalSeconds : -1, "s", "Age of last completed probe; -1 means none.");
        security = Meter.CreateCounter<long>("taskflow.security.signals", "{signal}", "Operational security outcomes; not an attributed or durable audit store.");
    }
    private Histogram<double> Duration(string name) => Meter.CreateHistogram<double>(name, "s", advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = [.005, .01, .025, .05, .1, .25, .5, 1, 2, 5, 10, 30] });
    public static string Method(string method) => method is "GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS" or "TRACE" or "CONNECT" ? method : "_OTHER";
    public static string Route(HttpContext context)
    {
        var endpoint = context.GetEndpoint() ?? context.Features.Get<IExceptionHandlerPathFeature>()?.Endpoint;
        // Only server-registered MVC templates are dimensions; never raw paths,
        // route values, fallback path captures, headers or query strings.
        return endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>()?.AttributeRouteInfo?.Template ?? "unmatched";
    }
    public Activity? StartRequest(HttpContext context)
    {
        // Represent our own server span. Reuse only a validated upstream parent,
        // not the unexported ASP.NET span or user-supplied tracestate/baggage tags.
        ActivityContext.TryParse(context.Request.Headers["traceparent"].ToString(), null, true, out var parent);
        var span = Traces.StartActivity("http.request", ActivityKind.Server, parent);
        if (span is not null) span.TraceStateString = null;
        return span;
    }
    public void CompleteRequest(HttpContext context, Activity? span, double seconds, bool cancelled, bool unhandled)
    {
        var route = Route(context); var method = Method(context.Request.Method);
        int status = cancelled ? 0 : unhandled ? 500 : context.Response.StatusCode is >= 100 and <= 599 ? context.Response.StatusCode : 0;
        TagList tags = new() { { "http.request.method", method }, { "http.route", route }, { "http.response.status_code", status } };
        requests.Add(1, tags); requestDuration.Record(seconds, tags);
        string? error = cancelled ? "cancelled" : status >= 500 ? "server_error" : status >= 400 ? "client_error" : null;
        if (error is not null) { tags.Add("error.type", error); errors.Add(1, tags); }
        if (span is not null)
        {
            span.DisplayName = method + " " + route;
            span.SetTag("http.request.method", method); span.SetTag("http.route", route); span.SetTag("http.response.status_code", status);
            if (cancelled || status >= 500) { span.SetTag("error.type", cancelled ? "cancelled" : "server_error"); span.SetStatus(ActivityStatusCode.Error); }
            span.TraceStateString = null;
        }
        var auth = method == "POST" ? route.TrimStart('/') switch
        {
            "api/v1/auth/login" => "authentication.login",
            "api/v1/auth/register" => "identity.registration",
            "api/v1/auth/logout" => "authentication.logout",
            _ => null
        } : null;
        if (auth is not null) Signal(auth, status is >= 200 and < 300 ? "success" : status == 429 ? "rate_limited" : status == 409 ? "conflict" : status >= 500 || status == 0 ? "error" : "rejected");
        else if (status is 401 or 403) Signal(status == 401 ? "authentication.session_rejected" : "authorization.denied", "rejected");
    }
    private void Signal(string action, string outcome) => security.Add(1, new TagList { { "event.action", action }, { "event.outcome", outcome } });
    public void Readiness(bool? ready, string result, double seconds)
    {
        if (result is not ("ready" or "database.unreachable" or "database.schema_mismatch" or "database.configuration_missing" or "database.check_failed" or "cancelled")) result = "database.check_failed";
        if (ready.HasValue) { Volatile.Write(ref readiness, ready.Value ? 1 : 0); Interlocked.Exchange(ref lastCompletedProbe, Stopwatch.GetTimestamp()); }
        TagList tags = new() { { "result", result } };
        readinessChecks.Add(1, tags); readinessDuration.Record(seconds, tags);
    }
    public void Dispose() { Traces.Dispose(); Meter.Dispose(); }
}
