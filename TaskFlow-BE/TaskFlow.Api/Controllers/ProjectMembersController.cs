using Microsoft.AspNetCore.Authorization;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Features.Projects;

namespace TaskFlow.Api.Controllers;

[Authorize]
[ApiController, Route("api/v1/projects/{projectId}/members")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class ProjectMembersController(IProjectMembersService members) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<ProjectMemberList>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string projectId, CancellationToken cancellationToken)
    {
        if (!TryId(projectId, "projectId", out var id)) return ValidationProblem(ModelState);
        return Ok(await members.ListAsync(Actor(), id, cancellationToken));
    }
    [HttpPost]
    [ProducesResponseType<ProjectMemberDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Add(string projectId, AddProjectMemberRequest request, CancellationToken cancellationToken)
    {
        var projectValid = TryId(projectId, "projectId", out var id);
        var userValid = TryId(request.UserId, "userId", out var userId);
        if (!projectValid || !userValid) return ValidationProblem(ModelState);
        var member = await members.AddAsync(Actor(), id, userId, cancellationToken);
        return CreatedAtAction(nameof(List), new { projectId }, member);
    }
    [HttpDelete("{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Remove(string projectId, string userId, CancellationToken cancellationToken)
    {
        var projectValid = TryId(projectId, "projectId", out var id);
        var userValid = TryId(userId, "userId", out var user);
        if (!projectValid || !userValid) return ValidationProblem(ModelState);
        await members.RemoveAsync(Actor(), id, user, cancellationToken);
        return NoContent();
    }
    private bool TryId(string value, string field, out long id)
    {
        id = 0;
        if (value.Length > 0 && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0) return true;
        ModelState.AddModelError(field, "Enter a positive decimal ID within the signed BIGINT range."); return false;
    }
    private long? Actor() => long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
public sealed class AddProjectMemberRequest
{
    [Required] public string UserId { get; set; } = "";
    [Required, RegularExpression("^Member$")] public string Role { get; set; } = "Member";
}
