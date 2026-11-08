using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Application.Features.Dashboard;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Api.Controllers;

[Authorize, ApiController, Route("api/v2/workspaces")]
[ServiceFilter(typeof(WorkspaceReadFilter))]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class WorkspaceReadsController(IWorkspaceReads reads) : ControllerBase
{
    private long Actor() => Id(User.FindFirstValue(ClaimTypes.NameIdentifier));
    private static long Id(string? value) => WorkspaceReadFilter.TryId(value, out var id) ? id
        : throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceReadContext Scope(string workspaceId) => new(Actor(), Id(workspaceId));
    private void Query(params string[] allowed)
    {
        foreach (var pair in Request.Query)
            if (!allowed.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || pair.Value.Count != 1)
                ModelState.AddModelError(pair.Key, "Unsupported or repeated query parameter.");
    }

    [HttpGet, ProducesResponseType<WorkspacePage>(200)]
    public async Task<IActionResult> Workspaces([FromQuery] WorkspaceListQuery query, CancellationToken token)
    {
        Query("page", "pageSize");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.WorkspacesAsync(Actor(), query.Page, query.PageSize, token));
    }
    [HttpGet("{workspaceId}"), ProducesResponseType<WorkspaceDto>(200)]
    public async Task<IActionResult> Workspace(string workspaceId, CancellationToken token)
    {
        Query(); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.WorkspaceAsync(Scope(workspaceId), token));
    }
    [HttpGet("{workspaceId}/projects"), ProducesResponseType<ProjectPage>(200)]
    public async Task<IActionResult> Projects(string workspaceId, [FromQuery] ProjectListQuery query, CancellationToken token)
    {
        Query("page", "pageSize", "search", "sortBy", "sortDirection");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.ProjectsAsync(Scope(workspaceId), new(query.Page, query.PageSize, query.Search, query.SortBy, query.SortDirection), token));
    }
    [HttpGet("{workspaceId}/projects/{projectId}"), ProducesResponseType<ProjectDetailsDto>(200)]
    public async Task<IActionResult> Project(string workspaceId, string projectId, CancellationToken token)
    {
        Query(); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.ProjectAsync(Scope(workspaceId), Id(projectId), token));
    }
    [HttpGet("{workspaceId}/tasks/{taskId}"), ProducesResponseType<TaskDto>(200)]
    public async Task<IActionResult> Task(string workspaceId, string taskId, CancellationToken token)
    {
        Query(); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.TaskAsync(Scope(workspaceId), Id(taskId), token));
    }
    [HttpGet("{workspaceId}/projects/{projectId}/tasks"), ProducesResponseType<TaskPage>(200)]
    public async Task<IActionResult> Tasks(string workspaceId, string projectId, [FromQuery] TaskListQuery query, CancellationToken token)
    {
        Query("page", "pageSize", "search", "status", "priority", "assigneeId", "sortBy", "sortDirection");
        TaskState? status = null; TaskPriority? priority = null; long? assignee = null;
        if (Request.Query.ContainsKey("status"))
        {
            if (Enum.TryParse<TaskState>(query.Status, false, out var state) && Enum.IsDefined(state) && state.ToString() == query.Status) status = state;
            else ModelState.AddModelError("status", "Choose ToDo, InProgress or Done.");
        }
        if (Request.Query.ContainsKey("priority"))
        {
            if (Enum.TryParse<TaskPriority>(query.Priority, false, out var value) && Enum.IsDefined(value) && value.ToString() == query.Priority) priority = value;
            else ModelState.AddModelError("priority", "Choose Low, Medium or High.");
        }
        if (Request.Query.ContainsKey("assigneeId"))
        {
            if (WorkspaceReadFilter.TryId(query.AssigneeId, out var user)) assignee = user;
            else ModelState.AddModelError("assigneeId", "Supply a positive decimal ID or omit this filter.");
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.TasksAsync(Scope(workspaceId), Id(projectId), new(query.Page, query.PageSize, query.Search, status, priority, assignee, query.SortBy, query.SortDirection), token));
    }
    [HttpGet("{workspaceId}/projects/{projectId}/members"), ProducesResponseType<ProjectMemberList>(200)]
    public async Task<IActionResult> Members(string workspaceId, string projectId, CancellationToken token)
    {
        Query(); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.MembersAsync(Scope(workspaceId), Id(projectId), token));
    }
    [HttpGet("{workspaceId}/projects/{projectId}/activity"), ProducesResponseType<ActivityPage>(200)]
    public Task<IActionResult> ProjectActivity(string workspaceId, string projectId, [FromQuery] ActivityQuery query, CancellationToken token) =>
        ActivityResult(workspaceId, Id(projectId), query, token);
    [HttpGet("{workspaceId}/activity"), ProducesResponseType<ActivityPage>(200)]
    public Task<IActionResult> Activity(string workspaceId, [FromQuery] ActivityQuery query, CancellationToken token) => ActivityResult(workspaceId, null, query, token);
    private async Task<IActionResult> ActivityResult(string workspaceId, long? projectId, ActivityQuery query, CancellationToken token)
    {
        Query("page", "pageSize", "fromUtc", "toUtc");
        DateTime? Bound(string? text, string field)
        {
            if (text is null && !Request.Query.ContainsKey(field)) return null;
            if (text is not null && Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")
                && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.UtcDateTime.Year >= 1000) return parsed.UtcDateTime;
            ModelState.AddModelError(field, "Enter an ISO timestamp with an explicit offset."); return null;
        }
        var from = Bound(query.FromUtc, "fromUtc"); var to = Bound(query.ToUtc, "toUtc");
        if (from > to) ModelState.AddModelError("fromUtc", "The start must not be later than the end.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.ActivityAsync(Scope(workspaceId), projectId, new(query.Page, query.PageSize, from, to), token));
    }
    [HttpGet("{workspaceId}/dashboard"), ProducesResponseType<DashboardDto>(200)]
    public async Task<IActionResult> Dashboard(string workspaceId, CancellationToken token)
    {
        Query(); if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await reads.DashboardAsync(Scope(workspaceId), token));
    }
}

public sealed class WorkspaceListQuery : IValidatableObject
{
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if ((long)(Page - 1) * PageSize > int.MaxValue) yield return new ValidationResult("Page offset is too large.", [nameof(Page)]);
    }
}
