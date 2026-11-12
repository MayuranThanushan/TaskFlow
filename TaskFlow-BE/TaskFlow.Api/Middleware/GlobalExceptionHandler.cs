using TaskFlow.Application.Features.Projects;
using Microsoft.AspNetCore.Diagnostics;

namespace TaskFlow.Api.Middleware;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is TaskFlow.Application.Features.Workspaces.InvitationException invitation)
        {
            if (invitation.Status == 429 && invitation.RetryAfterSeconds is >= 1 and <= 3600)
                context.Response.Headers.RetryAfter = invitation.RetryAfterSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var problem = ProblemResponses.Create(context, invitation.Status); problem.Extensions["code"] = invitation.Code;
            problem.Title = "Invitation action could not be completed."; await ProblemResponses.WriteAsync(context, problem); return true;
        }
        if (exception is TaskFlow.Application.Features.Workspaces.PilotQuotaException)
        {
            var problem = ProblemResponses.Create(context, 409); problem.Extensions["code"] = "quota.exceeded";
            problem.Title = "Pilot capacity reached. Contact your company owner or operations team.";
            await ProblemResponses.WriteAsync(context, problem); return true;
        }
        if (exception is ResourceVersionException || exception is Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            var conflict = exception as ResourceVersionException;
            var problem = ProblemResponses.Create(context, conflict?.Status ?? 409);
            problem.Extensions["code"] = conflict?.Code ?? "resource.conflict";
            problem.Title = conflict?.Status == 428 ? "Reload the record and supply X-Expected-Version."
                : conflict?.Status == 400 ? "Supply a valid X-Expected-Version revision."
                : "This record changed. Compare the latest version before saving again.";
            await ProblemResponses.WriteAsync(context, problem); return true;
        }
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
            failureCode, exception.GetType().Name, context.TraceIdentifier);
        await ProblemResponses.WriteAsync(context, ProblemResponses.Create(context, StatusCodes.Status500InternalServerError));
        return true;
    }
}
