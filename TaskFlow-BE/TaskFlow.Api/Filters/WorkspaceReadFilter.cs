using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using TaskFlow.Api.Middleware;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Filters;

public sealed class WorkspaceReadFilter(IConfiguration configuration, IWorkspaceReads reads) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        if (!configuration.GetValue("WorkspaceReads:Enabled", false))
        {
            context.Result = Missing(context.HttpContext); return;
        }
        if (context.RouteData.Values.TryGetValue("workspaceId", out var raw))
        {
            var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
            if (!TryId(text, out var workspace)) { context.Result = Missing(context.HttpContext); return; }
            if (!TryId(context.HttpContext.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
            { context.Result = new UnauthorizedResult(); return; }
            // Runs before binding/automatic validation. An inaccessible company cannot be probed
            // through differing filter-validation responses. Services recheck in their read snapshot.
            await reads.AuthorizeAsync(new(actor, workspace), context.HttpContext.RequestAborted);
        }
        await next();
    }
    public static bool TryId(string? value, out long id)
    {
        id = 0;
        return !string.IsNullOrEmpty(value) && value.All(char.IsAsciiDigit)
            && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
    private static ObjectResult Missing(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return new(ProblemResponses.Create(context, 404)) { StatusCode = 404, ContentTypes = { "application/problem+json" } };
    }
}
