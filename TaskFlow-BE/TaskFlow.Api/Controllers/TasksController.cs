using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Domain.Entities;
namespace TaskFlow.Api.Controllers;

[ApiController]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TasksController(ITaskService tasks) : ControllerBase
{
    [HttpPost("api/v1/projects/{projectId}/tasks")]
    public async Task<IActionResult> Create(string projectId, CreateTaskRequest request, CancellationToken cancellationToken)
    {
        TryId(projectId, "projectId", out var project);
        long? assignee = null; DateTime? due = null;
        if (request.AssigneeId is not null && TryId(request.AssigneeId, "assigneeId", out var user)) assignee = user;
        if (request.DueDateUtc is not null)
        {
            if (!Regex.IsMatch(request.DueDateUtc, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")
                || !DateTimeOffset.TryParse(request.DueDateUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.UtcDateTime.Year < 1000)
                ModelState.AddModelError("dueDateUtc", "Enter an ISO timestamp with Z or an explicit offset, within UTC years 1000-9999.");
            else due = new DateTime(parsed.UtcTicks - parsed.UtcTicks % 10, DateTimeKind.Utc);
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var task = await tasks.CreateAsync(Actor(), project, new(request.Title, request.Description, request.Priority, assignee, due), cancellationToken);
        return CreatedAtAction(nameof(Get), new { taskId = task.Id }, task);
    }
    [HttpGet("api/v1/tasks/{taskId}")]
    public async Task<IActionResult> Get(string taskId, CancellationToken cancellationToken)
    {
        if (!TryId(taskId, "taskId", out var id)) return ValidationProblem(ModelState);
        return Ok(await tasks.GetAsync(Actor(), id, cancellationToken));
    }
    [HttpGet("api/v1/projects/{projectId}/tasks")]
    public async Task<IActionResult> List(string projectId, [FromQuery] TaskListQuery query, CancellationToken cancellationToken)
    {
        TryId(projectId, "projectId", out var project);
        string[] allowed = ["page", "pageSize", "search", "status", "priority", "assigneeId", "sortBy", "sortDirection"];
        foreach (var pair in Request.Query)
            if (!allowed.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) || pair.Value.Count != 1) ModelState.AddModelError(pair.Key, "Unsupported or repeated query parameter.");
        if ((long)(query.Page - 1) * query.PageSize > int.MaxValue) ModelState.AddModelError("page", "The requested page exceeds the supported range.");
        foreach (var field in new[] { "status", "priority", "assigneeId" })
            if (Request.Query.ContainsKey(field) && string.IsNullOrWhiteSpace(Request.Query[field])) ModelState.AddModelError(field, "Supply a valid filter value or omit this filter.");
        long? assignee = null;
        if (query.AssigneeId is not null && TryId(query.AssigneeId, "assigneeId", out var user)) assignee = user;
        TaskState? status = null; TaskPriority? priority = null;
        if (query.Status is not null) { if (Enum.TryParse<TaskState>(query.Status, false, out var value) && Enum.IsDefined(value) && value.ToString() == query.Status) status = value; else ModelState.AddModelError("status", "Choose ToDo, InProgress or Done."); }
        if (query.Priority is not null) { if (Enum.TryParse<TaskPriority>(query.Priority, false, out var value) && Enum.IsDefined(value) && value.ToString() == query.Priority) priority = value; else ModelState.AddModelError("priority", "Choose Low, Medium or High."); }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await tasks.ListAsync(Actor(), project, new(query.Page, query.PageSize, query.Search, status, priority, assignee, query.SortBy, query.SortDirection), cancellationToken));
    }
    private bool TryId(string value, string field, out long id)
    {
        id = 0;
        if (value.Length > 0 && value.All(char.IsAsciiDigit) && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0) return true;
        ModelState.AddModelError(field, "Enter a positive decimal ID within the signed BIGINT range."); return false;
    }
    private long? Actor() => long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}
public sealed class CreateTaskRequest
{
    private string title = ""; private string? description;
    [Required, StringLength(200)] public string Title { get => title; set => title = value?.Trim() ?? ""; }
    [StringLength(10_000)] public string? Description { get => description; set => description = string.IsNullOrWhiteSpace(value) ? null : value.Trim(); }
    [EnumDataType(typeof(TaskPriority))] public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public string? AssigneeId { get; set; }
    public string? DueDateUtc { get; set; }
}
public sealed class TaskListQuery
{
    private string? search;
    [Range(1, int.MaxValue)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 20;
    [StringLength(200)] public string? Search { get => search; set => search = value?.Trim(); }
    public string? Status { get; set; }
    public string? Priority { get; set; }
    public string? AssigneeId { get; set; }
    [Required, RegularExpression("^(title|priority|dueDateUtc|createdAtUtc)$")] public string SortBy { get; set; } = "createdAtUtc";
    [Required, RegularExpression("^(asc|desc)$")] public string SortDirection { get; set; } = "desc";
}
