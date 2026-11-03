using System.Diagnostics.Metrics;
using TaskFlow.Infrastructure.Outbox;
namespace TaskFlow.Tests;
public sealed class OutboxMetricsTests
{
    [Fact]
    public void WorkerMetricsReportObservedStateWithoutTenantDimensions()
    {
        var samples = new Dictionary<string, double>(); var units = new Dictionary<string, string?>(); using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => { if (instrument.Meter.Name == "TaskFlow.Outbox") { units[instrument.Name] = instrument.Unit; owner.EnableMeasurementEvents(instrument); } };
        listener.SetMeasurementEventCallback<int>((i, value, tags, _) => { Assert.Equal(0, tags.Length); samples[i.Name] = value; });
        listener.SetMeasurementEventCallback<long>((i, value, tags, _) => { Assert.Equal(0, tags.Length); samples[i.Name] = value; });
        listener.SetMeasurementEventCallback<double>((i, value, tags, _) => { Assert.Equal(0, tags.Length); samples[i.Name] = value; });
        listener.Start(); using var metrics = new OutboxMetrics(); listener.RecordObservableInstruments(); Assert.Equal(0, samples["taskflow.worker.ready"]); Assert.False(samples.ContainsKey("taskflow.worker.outbox.pending"));
        metrics.Observed(new(2, 1, 4, 12)); metrics.Attempt(); listener.RecordObservableInstruments();
        Assert.Equal(1, samples["taskflow.worker.ready"]); Assert.Equal(3, samples["taskflow.worker.outbox.pending"]); Assert.Equal(4, samples["taskflow.worker.outbox.dead_letter"]); Assert.Equal(12, samples["taskflow.worker.outbox.oldest.age"]); Assert.Equal(1, samples["taskflow.worker.delivery.attempts"]);
        using var contract = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Contracts/worker-telemetry.json")));
        var expected = contract.RootElement.GetProperty("implemented").EnumerateArray().ToDictionary(x => x.GetProperty("name").GetString()!, x => x.GetProperty("unit").GetString());
        Assert.Equal(expected.OrderBy(x => x.Key), units.OrderBy(x => x.Key));
        metrics.Unavailable(); listener.RecordObservableInstruments(); Assert.Equal(0, samples["taskflow.worker.ready"]);
    }
}
