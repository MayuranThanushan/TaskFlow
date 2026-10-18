using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace TaskFlow.Api.Middleware;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        // Keep exception messages and request payloads out of routine logs.
        logger.LogError("Unhandled API failure {ExceptionType}; trace {TraceId}",
            exception.GetType().Name, Activity.Current?.Id ?? context.TraceIdentifier);
        await ProblemResponses.WriteAsync(context, ProblemResponses.Create(context, StatusCodes.Status500InternalServerError));
        return true;
    }
}
