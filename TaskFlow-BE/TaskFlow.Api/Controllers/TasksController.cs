using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Text.Json;
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
        due = Deadline(request.DueDateUtc);
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
    [HttpPatch("api/v1/tasks/{taskId}")]
    public async Task<IActionResult> Update(string taskId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        TryId(taskId, "taskId", out var id);
        var fields = Fields(body, ["title", "description", "priority", "assigneeId", "dueDateUtc"]);
        FieldChange<string?> title = default, description = default;
        FieldChange<TaskPriority> priority = default;
        FieldChange<long?> assignee = default; FieldChange<DateTime?> due = default;
        if (fields.TryGetValue("title", out var t)) title = new(true, Text(t, "title", false, 200));
        if (fields.TryGetValue("description", out var d)) description = new(true, Text(d, "description", true, 10_000));
        if (fields.TryGetValue("priority", out var p))
        {
            if (p.ValueKind != JsonValueKind.String || !Enum.TryParse<TaskPriority>(p.GetString(), false, out var value) || !Enum.IsDefined(value) || value.ToString() != p.GetString())
                ModelState.AddModelError("priority", "Choose Low, Medium or High.");
            else priority = new(true, value);
        }
        if (fields.TryGetValue("assigneeId", out var a))
        {
            long? user = null;
            if (a.ValueKind != JsonValueKind.Null)
            {
                if (a.ValueKind != JsonValueKind.String) ModelState.AddModelError("assigneeId", "Supply a decimal string ID or null.");
                else if (TryId(a.GetString()!, "assigneeId", out var parsed)) user = parsed;
            }
            assignee = new(true, user);
        }
        if (fields.TryGetValue("dueDateUtc", out var date))
        {
            if (date.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) ModelState.AddModelError("dueDateUtc", "Supply an ISO timestamp or null.");
            else due = new(true, Deadline(date.ValueKind == JsonValueKind.Null ? null : date.GetString()));
        }
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await tasks.UpdateAsync(Actor(), id, new(title, description, priority, assignee, due), cancellationToken));
    }
    [HttpPatch("api/v1/tasks/{taskId}/status")]
    public async Task<IActionResult> SetStatus(string taskId, [FromBody] JsonElement body, CancellationToken cancellationToken)
    {
        TryId(taskId, "taskId", out var id); var fields = Fields(body, ["status"]); var status = TaskState.ToDo;
        if (!fields.TryGetValue("status", out var value) || value.ValueKind != JsonValueKind.String
            || !Enum.TryParse(value.GetString(), false, out status) || !Enum.IsDefined(status) || status.ToString() != value.GetString())
            ModelState.AddModelError("status", "Choose ToDo, InProgress or Done.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        return Ok(await tasks.SetStatusAsync(Actor(), id, status, cancellationToken));
    }
    [HttpDelete("api/v1/tasks/{taskId}")]
    public async Task<IActionResult> Delete(string taskId, CancellationToken cancellationToken)
    {
        if (!TryId(taskId, "taskId", out var id)) return ValidationProblem(ModelState);
        await tasks.DeleteAsync(Actor(), id, cancellationToken); return NoContent();
    }
    private Dictionary<string, JsonElement> Fields(JsonElement body, string[] allowed)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object) { ModelState.AddModelError("body", "Supply a JSON object."); return fields; }
        foreach (var property in body.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) ModelState.AddModelError(property.Name, "Unsupported field.");
            if (!fields.TryAdd(property.Name, property.Value)) ModelState.AddModelError(property.Name, "Duplicate field.");
        }
        if (fields.Count == 0) ModelState.AddModelError("body", "Supply at least one editable field.");
        return fields;
    }
    private string? Text(JsonElement value, string field, bool nullable, int limit)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) { ModelState.AddModelError(field, "Supply a text value."); return null; }
        var text = value.GetString()!.Trim();
        if (text.Length > limit || !nullable && text.Length == 0) ModelState.AddModelError(field, $"Enter {(nullable ? "up to" : "between 1 and")} {limit} characters.");
        return nullable && text.Length == 0 ? null : text;
    }
    private DateTime? Deadline(string? text)
    {
        if (text is null) return null;
        if (!Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) || parsed.UtcDateTime.Year < 1000)
        { ModelState.AddModelError("dueDateUtc", "Enter an ISO timestamp with Z or an explicit offset, within UTC years 1000-9999."); return null; }
        return new DateTime(parsed.UtcTicks - parsed.UtcTicks % 10, DateTimeKind.Utc);
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
