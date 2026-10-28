using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TaskFlow.Api.Middleware;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Filters;

public sealed class WorkspaceLifecycleFilter(IConfiguration config, IWorkspaceReads reads) : IAsyncAuthorizationFilter, IOrderedFilter
{
    public int Order => 400;
    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        if (!config.GetValue("WorkspaceLifecycle:Enabled", false) || !config.GetValue("WorkspaceReads:Enabled", false) || !config.GetValue("WorkspaceWrites:Enabled", false))
        {
            context.Result = new ObjectResult(ProblemResponses.Create(context.HttpContext, 404)) { StatusCode = 404 }; return;
        }
        if (!WorkspaceReadFilter.TryId(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) { context.Result = new UnauthorizedResult(); return; }
        if (!WorkspaceReadFilter.TryId(Convert.ToString(context.RouteData.Values["workspaceId"]), out var workspace))
        { context.Result = new ObjectResult(ProblemResponses.Create(context.HttpContext, 404)) { StatusCode = 404 }; return; }
        await reads.AuthorizeAsync(new(actor, workspace), context.HttpContext.RequestAborted);
    }
}

// Company lifecycle must never be bypassed by a legacy resource route. Identity/health remain available.
public sealed class LegacyResourceFilter(IConfiguration config) : IAuthorizationFilter, IOrderedFilter
{
    public int Order => 400;
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        if (!config.GetValue("WorkspaceLifecycle:Enabled", false)) return;
        var path = context.HttpContext.Request.Path;
        if (new[] { "/api/v1/projects", "/api/v1/tasks", "/api/v1/dashboard", "/api/v1/activity" }.Any(prefix => path.StartsWithSegments(prefix)))
            context.Result = new ObjectResult(ProblemResponses.Create(context.HttpContext, 404)) { StatusCode = 404 };
    }
}
