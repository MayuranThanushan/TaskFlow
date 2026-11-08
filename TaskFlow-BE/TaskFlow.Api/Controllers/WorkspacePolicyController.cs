using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Controllers;

[Authorize, ApiController, Route("api/v2/workspaces/{workspaceId}/policy")]
[ServiceFilter(typeof(WorkspaceLifecycleFilter), Order = 400)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkspacePolicyController(IWorkspacePolicy policy) : ControllerBase
{
    private static long Id(string? value) => WorkspaceReadFilter.TryId(value, out var id) ? id : throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceWriteContext Scope(string id) => new(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)), Id(id));
    private bool Query()
    {
        foreach (var pair in Request.Query) ModelState.AddModelError(pair.Key, "Query parameters are not supported.");
        return ModelState.IsValid;
    }
    [HttpGet, ProducesResponseType<WorkspacePolicyDto>(200)]
    public async Task<IActionResult> Get(string workspaceId, CancellationToken token)
    { if (!Query()) return ValidationProblem(ModelState); var scope = Scope(workspaceId); return Ok(await policy.ReadPolicyAsync(new(scope.ActorId, scope.WorkspaceId), token)); }
    [HttpPut, EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> Update(string workspaceId, WorkspacePolicyRequest request, CancellationToken token)
    {
        if (!Query()) return ValidationProblem(ModelState);
        await policy.ChangePolicyAsync(Scope(workspaceId), new(request.ExpectedVersion, request.AuditRetentionDays ?? 0, request.DeletedContentRetentionDays ?? 0,
            request.LegalHold ?? false, request.AuditExportsEnabled ?? false, request.Password), token); return NoContent();
    }
}
public sealed class WorkspacePolicyRequest
{
    [Required, StringLength(19)] public string ExpectedVersion { get; set; } = "";
    [Required] public int? AuditRetentionDays { get; set; }
    [Required] public int? DeletedContentRetentionDays { get; set; }
    [Required] public bool? LegalHold { get; set; }
    [Required] public bool? AuditExportsEnabled { get; set; }
    [Required, StringLength(128)] public string Password { get; set; } = "";
}
