using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Api.Controllers;

[Authorize, ApiController, Route("api/v2/workspaces/{workspaceId}")]
[ServiceFilter(typeof(WorkspaceLifecycleFilter), Order = 400)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkspaceLifecycleController(IWorkspaceLifecycle lifecycle) : ControllerBase
{
    private static long Id(string? text) => WorkspaceReadFilter.TryId(text, out var id) ? id : throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceWriteContext Scope(string workspaceId) => new(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)), Id(workspaceId));
    private bool Query(bool paging = false)
    {
        foreach (var entry in Request.Query)
            if (!paging || entry.Key is not ("page" or "pageSize") || entry.Value.Count != 1) ModelState.AddModelError(entry.Key, "Unsupported or repeated query parameter.");
        return ModelState.IsValid;
    }
    [HttpGet("members"), ProducesResponseType<WorkspaceMemberPage>(200)]
    public async Task<IActionResult> Members(string workspaceId, [FromQuery] WorkspaceListQuery query, CancellationToken token)
    { if (!Query(true)) return ValidationProblem(ModelState); var c = Scope(workspaceId); return Ok(await lifecycle.MembersAsync(new(c.ActorId, c.WorkspaceId), query.Page, query.PageSize, token)); }
    [HttpGet("audit"), ProducesResponseType<WorkspaceAuditPage>(200)]
    public async Task<IActionResult> Audit(string workspaceId, [FromQuery] WorkspaceListQuery query, CancellationToken token)
    { if (!Query(true)) return ValidationProblem(ModelState); var c = Scope(workspaceId); return Ok(await lifecycle.AuditAsync(new(c.ActorId, c.WorkspaceId), query.Page, query.PageSize, token)); }
    [HttpPatch("members/{userId}/role"), EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> Role(string workspaceId, string userId, WorkspaceRoleRequest request, CancellationToken token)
    { if (!Query()) return ValidationProblem(ModelState); if (!Enum.TryParse<WorkspaceRole>(request.Role, false, out var role) || !Enum.IsDefined(role) || role.ToString() != request.Role) return BadRole(); await lifecycle.SetRoleAsync(Scope(workspaceId), Id(userId), role, request.Password, token); return NoContent(); }
    [HttpPatch("members/{userId}/state"), ProducesResponseType(204)]
    public async Task<IActionResult> State(string workspaceId, string userId, WorkspaceStateRequest request, CancellationToken token)
    { if (!Query()) return ValidationProblem(ModelState); if (!Enum.TryParse<WorkspaceMembershipState>(request.State, false, out var state) || !Enum.IsDefined(state) || state.ToString() != request.State) return BadRole(); await lifecycle.SetStateAsync(Scope(workspaceId), Id(userId), state, token); return NoContent(); }
    [HttpPost("leave"), EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> Leave(string workspaceId, WorkspacePasswordRequest request, CancellationToken token)
    { if (!Query()) return ValidationProblem(ModelState); await lifecycle.LeaveAsync(Scope(workspaceId), request.Password, token); return NoContent(); }
    [HttpPost("ownership"), EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> TransferCompany(string workspaceId, WorkspaceTransferRequest request, CancellationToken token)
    { if (!Query()) return ValidationProblem(ModelState); await lifecycle.TransferCompanyAsync(Scope(workspaceId), Id(request.UserId), request.Password, token); return NoContent(); }
    [HttpPut("projects/{projectId}/members/{userId}"), ProducesResponseType(204)]
    public async Task<IActionResult> ProjectRole(string workspaceId, string projectId, string userId, WorkspaceProjectRoleRequest request, CancellationToken token)
    { if (!Query()) return ValidationProblem(ModelState); if (!Enum.TryParse<MembershipRole>(request.Role, false, out var role) || role is not (MembershipRole.Editor or MembershipRole.Viewer) || role.ToString() != request.Role) return BadRole(); await lifecycle.SetProjectRoleAsync(Scope(workspaceId), Id(projectId), Id(userId), role, token); return NoContent(); }
    [HttpPost("projects/{projectId}/ownership"), EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> TransferProject(string workspaceId, string projectId, WorkspaceTransferRequest request, CancellationToken token)
    { if (!Query()) return ValidationProblem(ModelState); await lifecycle.TransferProjectAsync(Scope(workspaceId), Id(projectId), Id(request.UserId), request.Password, token); return NoContent(); }
    private IActionResult BadRole() { ModelState.AddModelError("role", "Choose a supported role or state."); return ValidationProblem(ModelState); }
}
public class WorkspacePasswordRequest { [StringLength(128)] public string? Password { get; set; } }
public sealed class WorkspaceRoleRequest : WorkspacePasswordRequest { [Required] public string Role { get; set; } = ""; }
public sealed class WorkspaceStateRequest { [Required] public string State { get; set; } = ""; }
public sealed class WorkspaceProjectRoleRequest { [Required] public string Role { get; set; } = ""; }
public sealed class WorkspaceTransferRequest { [Required] public string UserId { get; set; } = ""; [Required, StringLength(128)] public string Password { get; set; } = ""; }
