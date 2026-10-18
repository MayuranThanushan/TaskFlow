using System.Diagnostics;
using TaskFlow.Application.Features.Projects;
using Microsoft.AspNetCore.Diagnostics;

namespace TaskFlow.Api.Middleware;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is ProjectAccessException denied)
        {
            var status = denied.Decision switch
            {
                ProjectAccessDecision.Unauthenticated => StatusCodes.Status401Unauthorized,
                ProjectAccessDecision.NotFound => StatusCodes.Status404NotFound,
                ProjectAccessDecision.Forbidden => StatusCodes.Status403Forbidden,
                _ => StatusCodes.Status500InternalServerError
            };
            await ProblemResponses.WriteAsync(context, ProblemResponses.Create(context, status));
            return true;
        }
        if (exception is MembershipException membership)
        {
            var status = membership.Failure == MembershipFailure.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status404NotFound;
            await ProblemResponses.WriteAsync(context, ProblemResponses.Create(context, status));
            return true;
        }
        // Keep exception messages and request payloads out of routine logs.
        logger.LogError("Unhandled API failure {ExceptionType}; trace {TraceId}",
            exception.GetType().Name, Activity.Current?.Id ?? context.TraceIdentifier);
        await ProblemResponses.WriteAsync(context, ProblemResponses.Create(context, StatusCodes.Status500InternalServerError));
        return true;
    }
}
