using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace TaskFlow.Api.Middleware;

public static class ProblemResponses
{
    public static ProblemDetails Create(HttpContext context, int status) => Enrich(context, new ProblemDetails
    {
        Status = status,
        Title = ReasonPhrases.GetReasonPhrase(status),
        Detail = status >= 500 ? "An unexpected error occurred. Please try again." : null
    });

    public static ProblemDetails Enrich(HttpContext context, ProblemDetails problem)
    {
        problem.Type ??= "about:blank";
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        return problem;
    }

    public static Task WriteAsync(HttpContext context, ProblemDetails problem)
    {
        context.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        context.Response.Headers.CacheControl = "no-store";
        // Preserve derived ValidationProblemDetails fields when writing outside MVC.
        return context.Response.WriteAsJsonAsync((object)problem,
            options: (System.Text.Json.JsonSerializerOptions?)null,
            contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }
}
