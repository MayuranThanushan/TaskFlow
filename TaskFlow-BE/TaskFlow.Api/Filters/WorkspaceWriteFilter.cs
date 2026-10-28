using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TaskFlow.Api.Middleware;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Filters;

public sealed class WorkspaceWriteFilter(IConfiguration configuration, IWorkspaceReads reads) : IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => 500;
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!configuration.GetValue("WorkspaceReads:Enabled", false) || !configuration.GetValue("WorkspaceWrites:Enabled", false))
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new ObjectResult(ProblemResponses.Create(context.HttpContext, 404))
                { StatusCode = 404, ContentTypes = { "application/problem+json" } }; return;
        }
        if (!WorkspaceReadFilter.TryId(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
        { context.Result = new UnauthorizedResult(); return; }
        if (context.RouteData.Values.ContainsKey("workspaceId"))
        {
            if (!WorkspaceReadFilter.TryId(Convert.ToString(context.RouteData.Values["workspaceId"]), out var workspace))
            { await ProblemResponses.WriteAsync(context.HttpContext, ProblemResponses.Create(context.HttpContext, 404)); context.Result = new EmptyResult(); return; }
            await reads.AuthorizeAsync(new(actor, workspace), context.HttpContext.RequestAborted);
        }
        if (context.HttpContext.Request.Query.Count != 0)
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            context.Result = new ObjectResult(ProblemResponses.Create(context.HttpContext, 400))
                { StatusCode = 400, ContentTypes = { "application/problem+json" } };
        }
    }
}
