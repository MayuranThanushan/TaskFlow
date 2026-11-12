using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Notifications;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Controllers;

[Authorize, ApiController, Route("api/v2/workspaces/{workspaceId}/notifications")]
[NotificationFeature, ServiceFilter(typeof(WorkspaceReadFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class NotificationsController(IUserNotifications notifications) : ControllerBase
{
    private WorkspaceReadContext Scope(string workspaceId)
    {
        if (!WorkspaceReadFilter.TryId(workspaceId, out var workspace) || !WorkspaceReadFilter.TryId(User.FindFirstValue(ClaimTypes.NameIdentifier), out var user)) throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        return new(user, workspace);
    }
    private IActionResult? Query(params string[] allowed)
    {
        if (Request.Query.Any(q => !allowed.Contains(q.Key, StringComparer.OrdinalIgnoreCase) || q.Value.Count != 1))
            return BadRequest(Middleware.ProblemResponses.Create(HttpContext, 400));
        return null;
    }
    [HttpGet, ProducesResponseType<NotificationPage>(200)]
    public async Task<IActionResult> List(string workspaceId, [FromQuery] NotificationListQuery query, CancellationToken token)
    {
        if (Query("page", "pageSize", "unreadOnly") is { } error) return error;
        if ((long)(query.Page - 1) * query.PageSize > 10000) { ModelState.AddModelError("page", "Notification offset must not exceed 10000."); return ValidationProblem(ModelState); }
        return Ok(await notifications.ListAsync(Scope(workspaceId), query.Page, query.PageSize, query.UnreadOnly, token));
    }
    [HttpGet("unread-count"), ProducesResponseType<NotificationCountDto>(200)]
    public async Task<IActionResult> Count(string workspaceId, CancellationToken token)
    {
        if (Query() is { } error) return error;
        return Ok(await notifications.CountAsync(Scope(workspaceId), token));
    }
    [HttpPatch("{notificationId}"), EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> SetRead(string workspaceId, string notificationId, NotificationReadRequest request, CancellationToken token)
    {
        if (Query() is { } error) return error;
        if (!WorkspaceReadFilter.TryId(notificationId, out var id)) throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        await notifications.SetReadAsync(Scope(workspaceId), id, request.IsRead!.Value, token); return NoContent();
    }
}
public sealed class NotificationListQuery
{
    [Range(1, 501)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    public bool UnreadOnly { get; set; }
}
public sealed class NotificationReadRequest
{
    [Required] public bool? IsRead { get; set; }
}
[AttributeUsage(AttributeTargets.Class)]
public sealed class NotificationFeatureAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (!new[] { "WorkspaceReads:Enabled", "WorkspaceWrites:Enabled", "WorkspaceLifecycle:Enabled", "Notifications:Enabled" }.All(k => config.GetValue<bool>(k))) context.Result = new NotFoundResult();
        return Task.CompletedTask;
    }
}
