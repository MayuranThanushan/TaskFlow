using System.ComponentModel.DataAnnotations;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Api.Contracts;

// Documentation shapes for the presence-aware JsonElement parsers. These are
// intentionally not MVC binding types: binding must not lose omitted vs null.
public sealed class TaskPatchRequest
{
    [StringLength(200, MinimumLength = 1)] public string Title { get; set; } = null!;
    [StringLength(10_000)] public string? Description { get; set; }
    public TaskPriority Priority { get; set; }
    public string? AssigneeId { get; set; }
    public string? DueDateUtc { get; set; }
}
public sealed class TaskStatusRequest
{
    [Required] public TaskState Status { get; set; }
}

// Wire shape emitted by ProblemResponses/MVC. Optional fields are not always
// emitted; it is not a replacement serializer for framework ProblemDetails.
public sealed class ApiProblem
{
    public string Type { get; set; } = null!;
    public string Title { get; set; } = null!;
    public int Status { get; set; }
    public string TraceId { get; set; } = null!;
    public string? Detail { get; set; }
    public string? Instance { get; set; }
    public Dictionary<string, string[]>? Errors { get; set; }
}
