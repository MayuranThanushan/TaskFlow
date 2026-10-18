using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc.Filters;
using TaskFlow.Api.Middleware;

namespace TaskFlow.Api.Filters;

public sealed class AntiforgeryProblemFilter(IAntiforgery antiforgery) : IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => 1000;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var method = context.HttpContext.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method) || HttpMethods.IsTrace(method)) return;
        try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
        catch (AntiforgeryValidationException)
        {
            var problem = new ValidationProblemDetails(new Dictionary<string, string[]>
            {
                ["csrf"] = ["A valid CSRF token is required. Refresh the token and retry."]
            }) { Status = 400, Title = "CSRF validation failed." };
            ProblemResponses.Enrich(context.HttpContext, problem);
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            var result = new BadRequestObjectResult(problem);
            result.ContentTypes.Add("application/problem+json");
            context.Result = result;
        }
    }
}
