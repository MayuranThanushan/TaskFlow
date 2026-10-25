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
        if (exception is TaskFlow.Application.Features.Tasks.TaskValidationException invalid)
        {
            var problem = new Microsoft.AspNetCore.Mvc.ValidationProblemDetails(new Dictionary<string, string[]> { [invalid.Field] = [invalid.Message] }) { Status = 400 };
            await ProblemResponses.WriteAsync(context, ProblemResponses.Enrich(context, problem));
            return true;
        }
        // Keep exception messages and request payloads out of routine logs.
        var failureCode = exception is TaskFlow.Infrastructure.Persistence.PersistenceConfigurationException
            ? "database.configuration_missing" : exception is MySql.Data.MySqlClient.MySqlException
                ? "database.operation_failed" : "api.unhandled";
        logger.LogError(new EventId(2000, "api.failure"),
            "API failure {FailureCode} ({ExceptionType}); trace {TraceId}",
            failureCode, exception.GetType().Name, Activity.Current?.Id ?? context.TraceIdentifier);
        await ProblemResponses.WriteAsync(context, ProblemResponses.Create(context, StatusCodes.Status500InternalServerError));
        return true;
    }
}
