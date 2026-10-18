using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Logging;
using TaskFlow.Api.Middleware;
namespace TaskFlow.Tests;

public class LoggingSafetyTests
{
    [Fact]
    public async Task RequestAndExceptionLogsRetainDiagnosticsWithoutPrivateRequestOrExceptionData()
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "safe-trace";
        context.Request.Method = "PATCH";
        context.Request.Path = "/api/v1/projects/private-id-canary";
        context.Request.QueryString = new("?search=private-query-canary");
        context.Request.Headers.Authorization = "Bearer private-auth-canary";
        context.Request.Headers.Cookie = "session=private-cookie-canary";
        context.Request.Headers["X-CSRF-TOKEN"] = "private-csrf-canary";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("private-body-canary"));
        context.Response.Body = new MemoryStream();
        context.SetEndpoint(new RouteEndpoint(_ => Task.CompletedTask,
            RoutePatternFactory.Parse("api/v1/projects/{projectId}"), 0, EndpointMetadataCollection.Empty, "project"));
        var requests = new CaptureLogger<RequestLoggingMiddleware>();
        await new RequestLoggingMiddleware(_ => Task.CompletedTask, requests).InvokeAsync(context);
        Assert.Contains("api/v1/projects/{projectId}", requests.Text);
        Assert.Contains("PATCH", requests.Text); Assert.Contains("200", requests.Text);
        var failures = new CaptureLogger<GlobalExceptionHandler>();
        Assert.True(await new GlobalExceptionHandler(failures).TryHandleAsync(context,
            new InvalidOperationException("private-exception-canary", new Exception("private-inner-canary")), default));
        Assert.Contains("InvalidOperationException", failures.Text); Assert.Contains("safe-trace", failures.Text);
        var logged = requests.Text + failures.Text;
        foreach (var secret in new[] { "private-id-canary", "private-query-canary", "private-auth-canary", "private-cookie-canary", "private-csrf-canary", "private-body-canary", "private-exception-canary", "private-inner-canary" })
            Assert.DoesNotContain(secret, logged);
    }

    private sealed class CaptureLogger<T> : ILogger<T>
    {
        private readonly List<string> messages = [];
        public string Text => string.Join("\n", messages);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            if (exception is not null) messages.Add(exception.ToString());
        }
    }
}
