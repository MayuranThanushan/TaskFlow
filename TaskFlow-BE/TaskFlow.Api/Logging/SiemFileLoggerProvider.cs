using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TaskFlow.Api.Logging;

// One JSON object per UTF-8 line. No scope, exception text, raw URLs or arbitrary state values.
public sealed class SiemFileLoggerProvider : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly string environment;
    private readonly long maxBytes;
    private readonly int retainedFiles;
    private readonly string instance = $"{Environment.ProcessId}-{Guid.NewGuid():N}";
    private string? currentFile;
    private DateOnly currentDay;
    private int sequence;
    private bool reportedFailure;
    private bool disposed;
    private static readonly HashSet<string> Fields = ["Method", "Route", "StatusCode", "ElapsedMs", "TraceId", "ExceptionType", "FailureCode"];

    public SiemFileLoggerProvider(string directory, string environment, long maxBytes = 10 * 1024 * 1024, int retainedFiles = 14)
    {
        if (maxBytes < 1024 || retainedFiles < 1 || retainedFiles > 1000)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "Use at least 1024 bytes and 1-1000 retained files.");
        this.directory = Path.GetFullPath(directory);
        this.environment = environment;
        this.maxBytes = maxBytes;
        this.retainedFiles = retainedFiles;
        Directory.CreateDirectory(this.directory); // Invalid permissions fail explicitly during startup.
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { lock (gate) disposed = true; }

    private void Write<TState>(string category, LogLevel level, EventId eventId, TState state, Exception? exception)
    {
        var properties = new Dictionary<string, object?>();
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
            foreach (var pair in values)
                if (Fields.Contains(pair.Key) && pair.Value is string or int or long or double)
                    properties[pair.Key] = pair.Value is string text ? text[..Math.Min(text.Length, 1024)] : pair.Value;
        var record = new Dictionary<string, object?>
        {
            ["@timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["schema.version"] = "1.0",
            ["service.name"] = "taskflow-api",
            ["service.environment"] = environment,
            ["process.pid"] = Environment.ProcessId,
            ["log.level"] = level.ToString().ToLowerInvariant(),
            ["log.logger"] = category,
            ["event.code"] = eventId.Id,
            ["event.action"] = eventId.Name ?? "application.log",
            ["message"] = eventId.Name ?? "application.log",
            ["trace.id"] = Activity.Current?.TraceId.ToString(),
            ["span.id"] = Activity.Current?.SpanId.ToString(),
        };
        foreach (var pair in properties)
            record[pair.Key switch
            {
                "Method" => "http.request.method", "Route" => "http.route",
                "StatusCode" => "http.response.status_code", "ElapsedMs" => "taskflow.duration_ms",
                "TraceId" => "taskflow.trace_id", "ExceptionType" => "error.type",
                "FailureCode" => "error.code", _ => pair.Key
            }] = pair.Value;
        if (exception is not null) record["error.type"] = exception.GetType().Name;
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record) + "\n");
        lock (gate)
        {
            if (disposed) return;
            try
            {
                var day = DateOnly.FromDateTime(DateTime.UtcNow);
                if (currentFile is null || currentDay != day || !File.Exists(currentFile) || new FileInfo(currentFile).Length + bytes.Length > maxBytes)
                {
                    currentDay = day;
                    currentFile = Path.Combine(directory, $"taskflow-{day:yyyyMMdd}-{instance}-{sequence++:D5}.ndjson");
                    using var created = new FileStream(currentFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }
                using (var stream = new FileStream(currentFile, FileMode.Append, FileAccess.Write, FileShare.Read))
                    stream.Write(bytes);
                foreach (var stale in new DirectoryInfo(directory).GetFiles("taskflow-*.ndjson")
                    .Where(file => file.FullName != currentFile)
                    .OrderByDescending(file => file.LastWriteTimeUtc).Skip(retainedFiles - 1))
                    stale.Delete();
                reportedFailure = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Logging errors must not mask the original API response or disclose filesystem diagnostics.
                if (!reportedFailure) Console.Error.WriteLine("TaskFlow LOG_WRITE_FAILED: file logging unavailable; check directory permissions/disk space.");
                reportedFailure = true;
            }
        }
    }

    private sealed class FileLogger(SiemFileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information && level != LogLevel.None
            && category.StartsWith("TaskFlow.", StringComparison.Ordinal);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level)) provider.Write(category, level, id, state, error);
        }
    }
}
