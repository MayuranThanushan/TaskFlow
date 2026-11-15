namespace TaskFlow.Domain.Entities;
public sealed class TaskComment : IVersionedResource
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long ProjectId { get; set; }
    public long TaskId { get; set; }
    public long AuthorId { get; set; }
    public string Body { get; set; } = "";
    public long Version { get; set; } = 1;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? EditedAtUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}
// Append-only metadata history; former comment bodies are intentionally not retained.
public sealed class CommentRevision
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long CommentId { get; set; }
    public long ActorId { get; set; }
    public long Version { get; set; }
    public string Action { get; set; } = "Created";
    public DateTime CreatedAtUtc { get; set; }
}
