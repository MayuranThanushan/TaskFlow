using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Api.Contracts;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Controllers;

[Authorize, ApiController, Route("api/v2/workspaces/{workspaceId}")]
[ServiceFilter(typeof(WorkspaceWriteFilter), Order = 500)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkspaceWritesController(IWorkspaceWrites writes, IWorkspaceDirectory directory, IConfiguration config) : ControllerBase
{
    private static long Id(string? value) => WorkspaceReadFilter.TryId(value, out var id) ? id : throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceWriteContext Scope(string workspaceId) => new(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)), Id(workspaceId));
    [HttpPost("/api/v2/workspaces"), ProducesResponseType<WorkspaceDto>(201)]
    public async Task<IActionResult> CreateWorkspace(WorkspaceCreateRequest request, CancellationToken token)
    {
        var workspace = await directory.CreateAsync(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)), request.Name, token);
        return Created("/api/v2/workspaces/" + workspace.Id, workspace);
    }
    [HttpPost("projects"), ProducesResponseType<ProjectDto>(201)]
    public async Task<IActionResult> CreateProject(string workspaceId, ProjectWriteRequest request, CancellationToken token)
    {
        var project = await writes.CreateProjectAsync(Scope(workspaceId), request.Name, request.Description, token);
        return Created("/api/v2/workspaces/" + Id(workspaceId) + "/projects/" + project.Id, project);
    }
    [HttpPut("projects/{projectId}"), ProducesResponseType<ProjectDto>(200)]
    public async Task<IActionResult> UpdateProject(string workspaceId, string projectId, ProjectWriteRequest request, CancellationToken token) =>
        Ok(await writes.UpdateProjectAsync(Scope(workspaceId), Id(projectId), request.Name, request.Description, token));
    [HttpDelete("projects/{projectId}"), ProducesResponseType(204)]
    public async Task<IActionResult> DeleteProject(string workspaceId, string projectId, CancellationToken token)
    { await writes.DeleteProjectAsync(Scope(workspaceId), Id(projectId), token); return NoContent(); }
    [HttpPost("projects/{projectId}/members"), ProducesResponseType<ProjectMemberDto>(201)]
    public async Task<IActionResult> AddMember(string workspaceId, string projectId, WorkspaceProjectMemberRequest request, CancellationToken token)
    {
        if (!WorkspaceReadFilter.TryId(request.UserId, out var user)) ModelState.AddModelError("userId", "Supply a positive decimal string ID.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        if (!Enum.TryParse<TaskFlow.Domain.Entities.MembershipRole>(request.Role, false, out var role) || !Enum.IsDefined(role) || role.ToString() != request.Role)
            ModelState.AddModelError("role", "Choose Editor or Viewer.");
        if (role != TaskFlow.Domain.Entities.MembershipRole.Member && !config.GetValue("WorkspaceLifecycle:Enabled", false))
            ModelState.AddModelError("role", "Project roles require the lifecycle preview.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var member = await writes.AddMemberAsync(Scope(workspaceId), Id(projectId), user, role, token);
        return Created("/api/v2/workspaces/" + Id(workspaceId) + "/projects/" + Id(projectId) + "/members", member);
    }
    [HttpDelete("projects/{projectId}/members/{userId}"), ProducesResponseType(204)]
    public async Task<IActionResult> RemoveMember(string workspaceId, string projectId, string userId, CancellationToken token)
    { await writes.RemoveMemberAsync(Scope(workspaceId), Id(projectId), Id(userId), token); return NoContent(); }
    [HttpPost("projects/{projectId}/tasks"), ProducesResponseType<TaskDto>(201)]
    public async Task<IActionResult> CreateTask(string workspaceId, string projectId, CreateTaskRequest request, CancellationToken token)
    {
        var parser = new TaskInputParser(ModelState); long? assignee = null;
        if (request.AssigneeId is not null)
        {
            if (WorkspaceReadFilter.TryId(request.AssigneeId, out var user)) assignee = user;
            else ModelState.AddModelError("assigneeId", "Supply a positive decimal string ID or null.");
        }
        var due = parser.Deadline(request.DueDateUtc); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var task = await writes.CreateTaskAsync(Scope(workspaceId), Id(projectId), new(request.Title, request.Description, request.Priority, assignee, due), token);
        return Created("/api/v2/workspaces/" + Id(workspaceId) + "/tasks/" + task.Id, task);
    }
    [HttpPatch("tasks/{taskId}"), ProducesResponseType<TaskDto>(200)]
    public async Task<IActionResult> UpdateTask(string workspaceId, string taskId, [FromBody] JsonElement body, CancellationToken token)
    {
        var patch = new TaskInputParser(ModelState).Patch(body); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await writes.UpdateTaskAsync(Scope(workspaceId), Id(taskId), patch, token));
    }
    [HttpPatch("tasks/{taskId}/status"), ProducesResponseType<TaskStatusDto>(200)]
    public async Task<IActionResult> Status(string workspaceId, string taskId, [FromBody] JsonElement body, CancellationToken token)
    {
        var status = new TaskInputParser(ModelState).Status(body); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await writes.SetStatusAsync(Scope(workspaceId), Id(taskId), status, token));
    }
    [HttpDelete("tasks/{taskId}"), ProducesResponseType(204)]
    public async Task<IActionResult> DeleteTask(string workspaceId, string taskId, CancellationToken token)
    { await writes.DeleteTaskAsync(Scope(workspaceId), Id(taskId), token); return NoContent(); }
}

public sealed class WorkspaceCreateRequest
{
    [Required, StringLength(150)] public string Name { get; set; } = "";
}

public sealed class WorkspaceProjectMemberRequest
{
    [Required] public string UserId { get; set; } = "";
    public string Role { get; set; } = "Member";
}
