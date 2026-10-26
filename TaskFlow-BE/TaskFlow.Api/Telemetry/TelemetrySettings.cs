using System.Globalization;

namespace TaskFlow.Api.Telemetry;

public sealed record TelemetrySettings(bool ExportEnabled, Uri? Endpoint, string Headers, double SampleRatio, int TimeoutMilliseconds, int MetricIntervalMilliseconds)
{
    public override string ToString() => "TelemetrySettings (configuration values withheld)";

    public static TelemetrySettings Read(IConfiguration config)
    {
        bool Flag(string name, bool fallback) => config["Telemetry:" + name] is not { } value ? fallback
            : bool.TryParse(value, out var parsed) ? parsed : throw Invalid(name);
        double Number(string name, double fallback, double min, double max) => config["Telemetry:" + name] is not { } value ? fallback
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed) && parsed >= min && parsed <= max ? parsed : throw Invalid(name);
        bool enabled = Flag("Otlp:Enabled", false);
        double ratio = Number("TraceSampleRatio", .1, 0, 1);
        int Whole(string name, int fallback, int min, int max)
        { var value = Number(name, fallback, min, max); return value == Math.Truncate(value) ? (int)value : throw Invalid(name); }
        int timeout = Whole("Otlp:TimeoutMilliseconds", 3000, 100, 30000);
        int interval = Whole("Otlp:MetricIntervalMilliseconds", 30000, 5000, 300000);
        if (!enabled) return new(false, null, "", ratio, timeout, interval);
        if (!Uri.TryCreate(config["Telemetry:Otlp:Endpoint"], UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https") || endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0
            || endpoint.Scheme == "http" && !(endpoint.IsLoopback && Flag("Otlp:AllowInsecureLoopback", false)))
            throw Invalid("Otlp:Endpoint");
        var headers = config["Telemetry:Otlp:Headers"] ?? "";
        if (headers.Length > 8192 || headers.Contains('\r') || headers.Contains('\n')
            || headers.Length > 0 && headers.Split(',').Any(h => h.IndexOf('=') < 1 || !System.Text.RegularExpressions.Regex.IsMatch(h[..h.IndexOf('=')].Trim(), "^[A-Za-z0-9_-]+$")))
            throw Invalid("Otlp:Headers");
        // Never include configured endpoint/header values in validation exceptions.
        return new(true, new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"), headers, ratio, timeout, interval);
    }
    private static InvalidOperationException Invalid(string field) => new("Invalid Telemetry:" + field + ". See docs/observability.md; configured values are withheld.");
}
