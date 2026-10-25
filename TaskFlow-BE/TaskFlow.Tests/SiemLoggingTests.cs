using System.Text.Json;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TaskFlow.Api.Logging;

namespace TaskFlow.Tests;

public sealed class SiemLoggingTests
{
    [Fact]
    public async Task MissingConnectionHasActionableCodeAndFailureRetainsOriginalRoute()
    {
        var directory = Path.Combine(Path.GetTempPath(), "taskflow-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:TaskFlow"] = "", ["FileLogging:Enabled"] = "true",
                    ["FileLogging:Directory"] = directory
                }));
            });
            using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/v1/health/ready")).StatusCode);
            var bootstrap = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", bootstrap.GetProperty("requestToken").GetString());
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "private-email-canary@example.test", password = "private-password-canary" });
            Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
            var records = Directory.GetFiles(directory, "*.ndjson").SelectMany(File.ReadAllLines).ToArray();
            Assert.Contains(records, line => line.Contains("database.configuration_missing"));
            var request = records.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.Contains(request, doc => doc.RootElement.TryGetProperty("http.route", out var route)
                    && route.GetString() == "api/v1/auth/login" && doc.RootElement.GetProperty("http.response.status_code").GetInt32() == 500);
            }
            finally { foreach (var doc in request) doc.Dispose(); }
            Assert.DoesNotContain("private-", string.Join("", records));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void ConcurrentEventsAreParseableAndRotatedWithoutPrivateStateOrExceptions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "taskflow-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new SiemFileLoggerProvider(directory, "Testing", 2048, 3);
            var log = provider.CreateLogger("TaskFlow.Api.Test");
            Parallel.For(0, 40, i => log.Log(LogLevel.Error, new EventId(2000, "api.failure"),
                new Dictionary<string, object?> { ["StatusCode"] = 500, ["TraceId"] = "safe-trace", ["Password"] = "private-password-canary" },
                new InvalidOperationException("private-exception-canary"), (_, e) => "private-message-canary"));
            var files = Directory.GetFiles(directory, "*.ndjson");
            Assert.InRange(files.Length, 1, 3);
            var records = files.SelectMany(File.ReadAllLines).ToArray();
            Assert.NotEmpty(records);
            foreach (var line in records)
            {
                using var json = JsonDocument.Parse(line);
                Assert.Equal("taskflow-api", json.RootElement.GetProperty("service.name").GetString());
                Assert.Equal(500, json.RootElement.GetProperty("http.response.status_code").GetInt32());
                Assert.Equal("safe-trace", json.RootElement.GetProperty("taskflow.trace_id").GetString());
                Assert.Equal("InvalidOperationException", json.RootElement.GetProperty("error.type").GetString());
                Assert.DoesNotContain("private-", line);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void FrameworkMessagesAndScopesDoNotEnterTheApplicationFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "taskflow-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var provider = new SiemFileLoggerProvider(directory, "Testing");
            provider.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command").LogError("private-sql-canary");
            Assert.Empty(Directory.GetFiles(directory));
            var logger = provider.CreateLogger("TaskFlow.Api.Test");
            using var scope = logger.BeginScope("private-scope-canary");
            logger.LogInformation(new EventId(1000, "http.request_completed"), "private-message-canary");
            Assert.DoesNotContain("private-", File.ReadAllText(Directory.GetFiles(directory).Single()));
            File.Delete(Directory.GetFiles(directory).Single());
            logger.LogInformation(new EventId(1000, "http.request_completed"), "recreated event");
            using var recovered = JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(directory).Single()));
            Assert.Equal("http.request_completed", recovered.RootElement.GetProperty("event.action").GetString());
        }
        finally { Directory.Delete(directory, true); }
    }
}
