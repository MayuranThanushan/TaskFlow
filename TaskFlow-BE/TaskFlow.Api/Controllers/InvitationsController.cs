using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Workspaces;
namespace TaskFlow.Api.Controllers;
[Authorize, ApiController, Route("api/v2/workspaces/{workspaceId}/invitations"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[InvitationFeature]
public sealed class InvitationsController(IWorkspaceInvitations invitations) : ControllerBase
{
    private WorkspaceWriteContext Scope(string workspaceId)
    {
        if (!WorkspaceReadFilter.TryId(workspaceId, out var workspace) || !WorkspaceReadFilter.TryId(User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) throw new InvitationException(404, "invitation.unavailable");
        return new(actor, workspace);
    }
    private static long Id(string value) => WorkspaceReadFilter.TryId(value, out var id) ? id : throw new InvitationException(404, "invitation.unavailable");
    private void Query(bool listing = false)
    {
        if (Request.Query.Any(q => !listing || q.Key is not ("page" or "pageSize" or "projectId") || q.Value.Count != 1)) throw new InvitationException(400, "invitation.invalid_query");
    }
    [HttpGet, ProducesResponseType<InvitationPage>(200)]
    public async Task<IActionResult> List(string workspaceId, [FromQuery] InvitationListQuery query, CancellationToken token)
    { Query(true); return Ok(await invitations.ListAsync(Scope(workspaceId), query.ProjectId is null ? null : Id(query.ProjectId), query.Page, query.PageSize, token)); }
    [HttpPost, EnableRateLimiting("governance"), ProducesResponseType<InvitationDto>(201)]
    public async Task<IActionResult> Create(string workspaceId, InvitationCreateRequest request, CancellationToken token)
    { Query(); var dto = await invitations.CreateAsync(Scope(workspaceId), request.Email, request.ProjectId is null ? null : Id(request.ProjectId), request.Role, token); return Created($"/api/v2/workspaces/{workspaceId}/invitations", dto); }
    [HttpPost("{invitationId}/resend"), EnableRateLimiting("governance"), ProducesResponseType<InvitationDto>(200)]
    public async Task<IActionResult> Resend(string workspaceId, string invitationId, InvitationVersionRequest request, CancellationToken token)
    { Query(); return Ok(await invitations.ResendAsync(Scope(workspaceId), Id(invitationId), Id(request.ExpectedVersion), token)); }
    [HttpPost("{invitationId}/revoke"), EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> Revoke(string workspaceId, string invitationId, InvitationVersionRequest request, CancellationToken token)
    { Query(); await invitations.RevokeAsync(Scope(workspaceId), Id(invitationId), Id(request.ExpectedVersion), token); return NoContent(); }
    [HttpPost("{invitationId}/preview"), EnableRateLimiting("governance"), ProducesResponseType<InvitationPreviewDto>(200)]
    public async Task<IActionResult> Preview(string workspaceId, string invitationId, InvitationTokenRequest request, CancellationToken token)
    { Query(); return Ok(await invitations.PreviewAsync(Scope(workspaceId), Id(invitationId), request.Token, false, token)); }
    [HttpPost("{invitationId}/accept"), EnableRateLimiting("governance"), ProducesResponseType<InvitationPreviewDto>(200)]
    public async Task<IActionResult> Accept(string workspaceId, string invitationId, InvitationTokenRequest request, CancellationToken token)
    { Query(); return Ok(await invitations.PreviewAsync(Scope(workspaceId), Id(invitationId), request.Token, true, token)); }
}
public sealed class InvitationCreateRequest
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(16)] public string Role { get; set; } = "Member";
    [StringLength(19)] public string? ProjectId { get; set; }
}
public sealed class InvitationVersionRequest
{
    [Required, RegularExpression("^[1-9][0-9]{0,18}$")] public string ExpectedVersion { get; set; } = "";
}
public sealed class InvitationTokenRequest
{
    [Required, RegularExpression("^[A-Za-z0-9_-]{43}$")] public string Token { get; set; } = "";
}
public sealed class InvitationListQuery
{
    [Range(1, 501)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    public string? ProjectId { get; set; }
}
[AttributeUsage(AttributeTargets.Class)]
public sealed class InvitationFeatureAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var config = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        if (!new[] { "WorkspaceReads:Enabled", "WorkspaceWrites:Enabled", "WorkspaceLifecycle:Enabled", "Invitations:Enabled" }.All(k => config.GetValue<bool>(k))) context.Result = new NotFoundResult();
        return Task.CompletedTask;
    }
}
