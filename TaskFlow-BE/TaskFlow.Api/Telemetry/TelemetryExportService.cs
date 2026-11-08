using System.Reflection;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace TaskFlow.Api.Telemetry;

public sealed class TelemetryExportService(TelemetrySettings settings, IHostEnvironment environment) : IHostedService, IDisposable
{
    public TracerProvider? Traces { get; private set; }
    public MeterProvider? Metrics { get; private set; }
    private int disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!settings.ExportEnabled) return Task.CompletedTask;
        var deployment = environment.EnvironmentName is "Development" or "Local" or "Testing" or "UAT" or "Production" ? environment.EnvironmentName : "Other";
        ResourceBuilder Resource() => ResourceBuilder.CreateEmpty().AddService("taskflow-api", serviceVersion: typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown", autoGenerateServiceInstanceId: false)
            .AddAttributes([new KeyValuePair<string, object>("deployment.environment.name", deployment)]);
        try
        {
            Traces = Sdk.CreateTracerProviderBuilder().SetResourceBuilder(Resource()).AddSource(ServiceTelemetry.SourceName)
                .SetSampler(new TraceIdRatioBasedSampler(settings.SampleRatio))
                .AddOtlpExporter(options => Configure(options, "v1/traces")).Build();
            Metrics = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(Resource()).AddMeter(ServiceTelemetry.SourceName)
                .SetExemplarFilter(ExemplarFilterType.AlwaysOff)
                .AddView(_ => new MetricStreamConfiguration
                {
                    TagKeys = ["http.request.method", "http.route", "http.response.status_code", "error.type", "result", "event.action", "event.outcome"],
                    CardinalityLimit = 512
                })
                .AddOtlpExporter((options, reader) =>
                {
                    Configure(options, "v1/metrics");
                    reader.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = settings.MetricIntervalMilliseconds;
                    reader.PeriodicExportingMetricReaderOptions.ExportTimeoutMilliseconds = settings.TimeoutMilliseconds;
                }).Build();
        }
        catch
        {
            Dispose();
            throw new InvalidOperationException("Telemetry exporter initialization failed. Check docs/observability.md; configured values are withheld.");
        }
        return Task.CompletedTask;
    }
    private void Configure(OtlpExporterOptions options, string signal)
    {
        options.Endpoint = new Uri(settings.Endpoint!, signal);
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.Headers = settings.Headers;
        options.TimeoutMilliseconds = settings.TimeoutMilliseconds;
        options.ExportProcessorType = ExportProcessorType.Batch;
        options.BatchExportProcessorOptions.MaxQueueSize = 2048;
        options.BatchExportProcessorOptions.MaxExportBatchSize = 256;
        options.BatchExportProcessorOptions.ScheduledDelayMilliseconds = 5000;
        options.HttpClientFactory = () => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Traces?.ForceFlush(settings.TimeoutMilliseconds);
        Metrics?.ForceFlush(settings.TimeoutMilliseconds);
        return Task.CompletedTask;
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Traces?.Dispose(); Metrics?.Dispose();
    }
}
