using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using TaskFlow.Api.Telemetry;

namespace TaskFlow.Tests;

public class TelemetryTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void ExportIsOptInAndIgnoresAmbientCollectorSettingsWhenDisabled()
    {
        var settings = TelemetrySettings.Read(Config(new() { ["Telemetry:Otlp:Endpoint"] = "not-a-url-private-canary", ["Telemetry:Otlp:Headers"] = "private-secret" }));
        Assert.False(settings.ExportEnabled); Assert.Null(settings.Endpoint); Assert.Empty(settings.Headers);
    }

    [Theory]
    [InlineData("http://collector.example:4318")]
    [InlineData("http://localhost:4318")]
    [InlineData("https://user:private-secret@collector.example")]
    [InlineData("https://collector.example?token=private-secret")]
    [InlineData("https://collector.example#private-secret")]
    [InlineData("file:///private-secret")]
    public void InvalidCollectorSettingsFailWithoutEchoingConfiguredValues(string endpoint)
    {
        var error = Assert.Throws<InvalidOperationException>(() => TelemetrySettings.Read(Config(new() { ["Telemetry:Otlp:Enabled"] = "true", ["Telemetry:Otlp:Endpoint"] = endpoint })));
        Assert.DoesNotContain(endpoint, error.Message); Assert.DoesNotContain("private-secret", error.ToString());
    }

    [Theory]
    [InlineData("TraceSampleRatio", "NaN")]
    [InlineData("TraceSampleRatio", "1.1")]
    [InlineData("Otlp:MetricIntervalMilliseconds", "0")]
    [InlineData("Otlp:TimeoutMilliseconds", "999999")]
    [InlineData("Otlp:TimeoutMilliseconds", "100.5")]
    [InlineData("Otlp:Enabled", "private-secret")]
    public void InvalidNumericOrBooleanSettingsFailClosed(string field, string value)
    {
        Assert.Throws<InvalidOperationException>(() => TelemetrySettings.Read(Config(new() { ["Telemetry:" + field] = value })));
    }

    [Fact]
    public void LoopbackHttpRequiresExplicitOptInAndCollectorHeadersRejectNewlines()
    {
        var values = new Dictionary<string, string?> { ["Telemetry:Otlp:Enabled"] = "true", ["Telemetry:Otlp:Endpoint"] = "http://127.0.0.1:4318/prefix", ["Telemetry:Otlp:AllowInsecureLoopback"] = "true" };
        Assert.Equal("http://127.0.0.1:4318/prefix/", TelemetrySettings.Read(Config(values)).Endpoint!.AbsoluteUri);
        values["Telemetry:Otlp:Headers"] = "x-api-key=private-header-canary";
        Assert.DoesNotContain("private-header-canary", TelemetrySettings.Read(Config(values)).ToString());
        values["Telemetry:Otlp:Headers"] = "Authorization=private-secret\r\nInjected=yes";
        var error = Assert.Throws<InvalidOperationException>(() => TelemetrySettings.Read(Config(values)));
        Assert.DoesNotContain("private-secret", error.Message);
    }

    [Fact]
    public void PublishedInstrumentsMatchTheDocumentedContractAndDoNotPretendAWorkerExists()
    {
        using var telemetry = new ServiceTelemetry(); var instruments = new Dictionary<string, string?>();
        using var listener = new MeterListener { InstrumentPublished = (i, _) => { if (ReferenceEquals(i.Meter, telemetry.Meter)) instruments.Add(i.Name, i.Unit); } };
        listener.Start();
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts/telemetry.json")));
        var expected = document.RootElement.GetProperty("implemented").EnumerateArray().ToDictionary(m => m.GetProperty("name").GetString()!, m => m.GetProperty("unit").GetString());
        Assert.Equal(expected.OrderBy(p => p.Key), instruments.OrderBy(p => p.Key));
        foreach (var metric in document.RootElement.GetProperty("reservedWorkerMetrics").EnumerateArray()) Assert.DoesNotContain(metric.GetProperty("name").GetString()!, instruments.Keys);
    }

    [Fact]
    public async Task RequestMetricsBoundMethodAndRouteAndCountErrorsWithoutPrivateDimensions()
    {
        using var telemetry = new ServiceTelemetry();
        var measurements = new List<(string Name, double Value, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener { InstrumentPublished = (i, l) => { if (ReferenceEquals(i.Meter, telemetry.Meter)) l.EnableMeasurementEvents(i); } };
        void Capture(Instrument i, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags) => measurements.Add((i.Name, value, tags.ToArray().ToDictionary(p => p.Key, p => p.Value)));
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) => Capture(i, v, tags));
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => Capture(i, v, tags));
        listener.Start();
        var context = new DefaultHttpContext(); context.Request.Method = "PRIVATE_METHOD_CANARY";
        context.Request.Path = "/private-path-canary"; context.Request.QueryString = new("?search=private-query-canary"); context.Request.Headers.Cookie = "private-cookie-canary";
        await new ServiceTelemetryMiddleware(c => { c.Response.StatusCode = 503; return Task.CompletedTask; }).InvokeAsync(context, telemetry);
        Assert.Contains(measurements, m => m.Name == "taskflow.http.requests" && m.Value == 1 && Equals(m.Tags["http.request.method"], "_OTHER") && Equals(m.Tags["http.route"], "unmatched"));
        Assert.Contains(measurements, m => m.Name == "taskflow.http.errors" && Equals(m.Tags["error.type"], "server_error"));
        Assert.Contains(measurements, m => m.Name == "taskflow.http.request.duration" && m.Value >= 0);
        Assert.DoesNotContain("canary", string.Join(" ", measurements.SelectMany(m => m.Tags.Select(p => p.Key + "=" + p.Value))), StringComparison.OrdinalIgnoreCase);
        Assert.All(measurements, m => Assert.All(m.Tags.Keys, key => Assert.Contains(key, new[] { "http.request.method", "http.route", "http.response.status_code", "error.type" })));
    }

    [Fact]
    public void ReadinessDistinguishesUnknownAndCancellationFromAnUnhealthyDatabase()
    {
        using var telemetry = new ServiceTelemetry(); var states = new List<int>(); var ages = new List<double>(); var results = new List<string>();
        using var listener = new MeterListener { InstrumentPublished = (i, l) => { if (ReferenceEquals(i.Meter, telemetry.Meter)) l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<int>((i, v, _, _) => { if (i.Name == "taskflow.database.readiness") states.Add(v); });
        listener.SetMeasurementEventCallback<double>((i, v, _, _) => { if (i.Name == "taskflow.database.readiness.age") ages.Add(v); });
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) => { if (i.Name.EndsWith("checks")) results.Add(tags.ToArray().Single().Value!.ToString()!); });
        listener.Start(); listener.RecordObservableInstruments(); Assert.Equal(-1, states.Last()); Assert.Equal(-1, ages.Last());
        telemetry.Readiness(false, "database.unreachable", .01); listener.RecordObservableInstruments(); Assert.Equal(0, states.Last()); Assert.True(ages.Last() >= 0);
        telemetry.Readiness(null, "cancelled", .01); listener.RecordObservableInstruments(); Assert.Equal(0, states.Last());
        telemetry.Readiness(true, "ready", .01); listener.RecordObservableInstruments(); Assert.Equal(1, states.Last());
        telemetry.Readiness(null, "private-host-canary", .01); Assert.Equal("database.check_failed", results.Last());
    }

    [Fact]
    public async Task CancellationAndUnhandledFailureAreRecordedWithoutSwallowingExceptions()
    {
        using var telemetry = new ServiceTelemetry(); var codes = new List<int>();
        using var listener = new MeterListener { InstrumentPublished = (i, l) => { if (ReferenceEquals(i.Meter, telemetry.Meter)) l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) => { if (i.Name == "taskflow.http.requests") codes.Add((int)tags.ToArray().Single(p => p.Key == "http.response.status_code").Value!); }); listener.Start();
        var cancelled = new DefaultHttpContext { RequestAborted = new CancellationToken(true) };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new ServiceTelemetryMiddleware(_ => throw new OperationCanceledException()).InvokeAsync(cancelled, telemetry));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ServiceTelemetryMiddleware(_ => throw new InvalidOperationException("private-exception-canary")).InvokeAsync(new DefaultHttpContext(), telemetry));
        Assert.Equal(new[] { 0, 500 }, codes);
    }

    [Fact]
    public async Task RealOtlpHttpExportCorrelatesResponseAndRedactsRequestAndTraceState()
    {
        var received = new ConcurrentQueue<(string Path, byte[] Bytes)>();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = builder.Build();
        collector.MapPost("/v1/{signal}", async (HttpContext c) => { using var body = new MemoryStream(); await c.Request.Body.CopyToAsync(body); received.Enqueue((c.Request.Path, body.ToArray())); c.Response.ContentType = "application/x-protobuf"; });
        await collector.StartAsync();
        await using var factory = Factory(new() { ["Telemetry:Otlp:Enabled"] = "true", ["Telemetry:Otlp:Endpoint"] = collector.Urls.Single(), ["Telemetry:Otlp:AllowInsecureLoopback"] = "true", ["Telemetry:TraceSampleRatio"] = "1" });
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("traceparent", "00-11111111111111111111111111111111-2222222222222222-01");
        client.DefaultRequestHeaders.Add("tracestate", "vendor=private-tracestate-canary"); client.DefaultRequestHeaders.Add("baggage", "customer=private-baggage-canary");
        client.DefaultRequestHeaders.Add("Cookie", "private-cookie-canary"); client.DefaultRequestHeaders.Add("Authorization", "Bearer private-auth-canary");
        var response = await client.GetAsync("/api/v1/health/live?private-query-canary=yes"); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var correlation = response.Headers.GetValues("X-Trace-Id").Single();
        Assert.Contains("11111111111111111111111111111111", correlation);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/projects/private-id-canary")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/v1/auth/login", new StringContent("private-body-canary", Encoding.UTF8, "application/json"))).StatusCode);
        factory.Services.GetRequiredService<ServiceTelemetry>().Meter.CreateCounter<long>("taskflow.test.dimension_filter").Add(1, new KeyValuePair<string, object?>("workspace.id", "private-workspace-canary"));
        Assert.True(factory.Services.GetRequiredService<TelemetryExportService>().Traces!.ForceFlush(10000));
        Assert.True(factory.Services.GetRequiredService<TelemetryExportService>().Metrics!.ForceFlush(10000));
        var traces = received.Where(r => r.Path == "/v1/traces").SelectMany(r => r.Bytes).ToArray();
        var metrics = received.Where(r => r.Path == "/v1/metrics").SelectMany(r => r.Bytes).ToArray();
        Assert.NotEmpty(traces); Assert.NotEmpty(metrics);
        Assert.Contains("taskflow.http.request.duration", Encoding.UTF8.GetString(metrics)); Assert.Contains("taskflow.database.readiness", Encoding.UTF8.GetString(metrics));
        Assert.Contains("api/v1/health/live", Encoding.UTF8.GetString(traces));
        Assert.True(traces.AsSpan().IndexOf(Convert.FromHexString(correlation.Split('-')[1])) >= 0);
        var payload = Encoding.UTF8.GetString(traces.Concat(metrics).ToArray());
        foreach (var secret in new[] { "private-query-canary", "private-tracestate-canary", "private-baggage-canary", "private-cookie-canary", "private-auth-canary", "private-id-canary", "private-body-canary", "private-workspace-canary" }) Assert.DoesNotContain(secret, payload);
        await collector.StopAsync();
    }

    [Fact]
    public async Task CollectorRejectionDoesNotFailBusinessRequests()
    {
        int exports = 0;
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var collector = builder.Build();
        collector.MapPost("/v1/{signal}", () => { Interlocked.Increment(ref exports); return Results.BadRequest(); });
        await collector.StartAsync();
        await using var factory = Factory(new() { ["Telemetry:Otlp:Enabled"] = "true", ["Telemetry:Otlp:Endpoint"] = collector.Urls.Single(), ["Telemetry:Otlp:AllowInsecureLoopback"] = "true", ["Telemetry:TraceSampleRatio"] = "1", ["Telemetry:Otlp:TimeoutMilliseconds"] = "100" });
        using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);
        // ForceFlush reports queue processing, not durable downstream acceptance.
        factory.Services.GetRequiredService<TelemetryExportService>().Traces!.ForceFlush(5000);
        factory.Services.GetRequiredService<TelemetryExportService>().Metrics!.ForceFlush(5000);
        Assert.True(Volatile.Read(ref exports) > 0);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);
        await collector.StopAsync();
    }

    [Fact]
    public void SecuritySignalsUseFixedActionOutcomeVocabularyWithoutIdentityDimensions()
    {
        using var telemetry = new ServiceTelemetry(); var signals = new List<Dictionary<string, object?>>();
        using var listener = new MeterListener { InstrumentPublished = (i, l) => { if (ReferenceEquals(i.Meter, telemetry.Meter)) l.EnableMeasurementEvents(i); } };
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) => { if (i.Name == "taskflow.security.signals") signals.Add(tags.ToArray().ToDictionary(p => p.Key, p => p.Value)); }); listener.Start();
        foreach (var (route, method, status) in new[] { ("api/v1/auth/login", "POST", 401), ("api/v1/auth/register", "POST", 409), ("api/v1/auth/logout", "POST", 204), ("api/v1/projects", "GET", 403), ("api/v1/projects", "GET", 401) })
        {
            var context = new DefaultHttpContext(); context.Request.Method = method; context.Response.StatusCode = status;
            var action = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor { AttributeRouteInfo = new Microsoft.AspNetCore.Mvc.Routing.AttributeRouteInfo { Template = route } };
            context.SetEndpoint(new Endpoint(_ => Task.CompletedTask, new EndpointMetadataCollection(action), "test"));
            telemetry.CompleteRequest(context, null, .01, false, false);
        }
        Assert.Equal(new[] { "authentication.login", "identity.registration", "authentication.logout", "authorization.denied", "authentication.session_rejected" }, signals.Select(s => s["event.action"]));
        Assert.Equal(new[] { "rejected", "conflict", "success", "rejected", "rejected" }, signals.Select(s => s["event.outcome"]));
        Assert.All(signals, signal => Assert.Equal(2, signal.Count));
    }

    [Fact]
    public async Task DisabledExportDoesNotRegisterNetworkProvidersOrExposeMetricsRoutes()
    {
        await using var factory = Factory(new()); using var client = factory.CreateClient(new() { BaseAddress = new("https://localhost") });
        Assert.Null(factory.Services.GetRequiredService<TelemetryExportService>().Traces); Assert.Null(factory.Services.GetRequiredService<TelemetryExportService>().Metrics);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/metrics")).StatusCode);
    }

    private static WebApplicationFactory<Program> Factory(Dictionary<string, string?> values)
    {
        values["ConnectionStrings:TaskFlow"] = ""; values["Logging:LogLevel:Default"] = "Critical";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b => { b.UseEnvironment("Testing"); b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(values)); });
    }
}
