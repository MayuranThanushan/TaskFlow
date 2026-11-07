using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Api.Middleware;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Api.Filters;

// After rollout/admission authorization and before JSON binding; services retain their own gates.
public sealed class MfaAdministrationFilter(MfaSettings settings, IServiceProvider services, TimeProvider clock) : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (settings.RequireAdministrators && http.Request.Path.StartsWithSegments("/api/v2/workspaces")
            && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
            && WorkspaceReadFilter.TryId(Convert.ToString(context.RouteData.Values.GetValueOrDefault("workspaceId")), out var workspace)
            && WorkspaceReadFilter.TryId(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor))
        {
            // Keep health/CSRF and disabled-policy requests independent of persistence configuration.
            var db = services.GetRequiredService<TaskFlowDbContext>();
            var administrator = await db.WorkspaceMemberships.AsNoTracking().AnyAsync(m => m.WorkspaceId == workspace && m.UserId == actor && m.State == WorkspaceMembershipState.Active && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Admin), http.RequestAborted);
            if (administrator && (MfaProof.Until(http.User, clock) is null || !await db.Users.AnyAsync(u => u.Id == actor && u.TwoFactorEnabled, http.RequestAborted)))
            {
                var problem = ProblemResponses.Create(http, 403); problem.Title = "Confirm your identity with an authenticator code in Account security before this company action."; problem.Extensions["code"] = "mfa.administration_required";
                context.Result = new ObjectResult(problem) { StatusCode = 403, ContentTypes = { "application/problem+json" } }; return;
            }
        }
        await next();
    }
}
