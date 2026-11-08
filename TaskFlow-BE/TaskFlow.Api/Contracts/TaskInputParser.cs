using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Api.Contracts;

// Presence-aware scoped PATCH parser; body fields never select actor, project or workspace.
public sealed class TaskInputParser(ModelStateDictionary state)
{
    public DateTime? Deadline(string? value)
    {
        if (value is null) return null;
        if (Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed.UtcDateTime.Year >= 1000)
            return new DateTime(parsed.UtcTicks - parsed.UtcTicks % 10, DateTimeKind.Utc);
        state.AddModelError("dueDateUtc", "Enter an ISO timestamp with an explicit offset."); return null;
    }
    public long? Assignee(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.String && WorkspaceReadFilter.TryId(value.GetString(), out var id)) return id;
        state.AddModelError("assigneeId", "Supply a positive decimal string ID or null."); return null;
    }
    private Dictionary<string, JsonElement> Fields(JsonElement body, string[] allowed)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (body.ValueKind != JsonValueKind.Object) { state.AddModelError("body", "Supply an object."); return fields; }
        foreach (var property in body.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal)) state.AddModelError(property.Name, "Unsupported field.");
            if (!fields.TryAdd(property.Name, property.Value)) state.AddModelError(property.Name, "Duplicate field.");
        }
        if (fields.Count == 0) state.AddModelError("body", "Supply at least one editable field.");
        return fields;
    }
    private string? Text(JsonElement value, string field, int limit, bool nullable)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) { state.AddModelError(field, "Supply text."); return null; }
        var text = value.GetString()!.Trim();
        if ((!nullable && text.Length == 0) || text.Length > limit) state.AddModelError(field, "Text is empty or exceeds the allowed length.");
        return nullable && text.Length == 0 ? null : text;
    }
    public TaskPatch Patch(JsonElement body)
    {
        var fields = Fields(body, ["title", "description", "priority", "assigneeId", "dueDateUtc"]);
        FieldChange<string?> title = default, description = default;
        FieldChange<TaskPriority> priority = default; FieldChange<long?> assignee = default; FieldChange<DateTime?> due = default;
        if (fields.TryGetValue("title", out var t)) title = new(true, Text(t, "title", 200, false));
        if (fields.TryGetValue("description", out var d)) description = new(true, Text(d, "description", 10_000, true));
        if (fields.TryGetValue("priority", out var p))
        {
            if (p.ValueKind == JsonValueKind.String && Enum.TryParse<TaskPriority>(p.GetString(), false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == p.GetString()) priority = new(true, parsed);
            else state.AddModelError("priority", "Choose Low, Medium or High.");
        }
        if (fields.TryGetValue("assigneeId", out var a)) assignee = new(true, Assignee(a));
        if (fields.TryGetValue("dueDateUtc", out var date))
        {
            if (date.ValueKind is JsonValueKind.Null or JsonValueKind.String) due = new(true, Deadline(date.ValueKind == JsonValueKind.Null ? null : date.GetString()));
            else state.AddModelError("dueDateUtc", "Supply a timestamp or null.");
        }
        return new(title, description, priority, assignee, due);
    }
    public TaskState Status(JsonElement body)
    {
        var fields = Fields(body, ["status"]);
        if (fields.TryGetValue("status", out var value) && value.ValueKind == JsonValueKind.String
            && Enum.TryParse<TaskState>(value.GetString(), false, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == value.GetString()) return parsed;
        state.AddModelError("status", "Choose ToDo, InProgress or Done."); return TaskState.ToDo;
    }
}
