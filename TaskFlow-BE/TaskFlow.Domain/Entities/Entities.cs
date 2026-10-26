namespace TaskFlow.Domain.Entities;

public interface IAuditable
{
    DateTime CreatedAtUtc { get; set; }
    DateTime UpdatedAtUtc { get; set; }
}

public abstract class AuditableEntity : IAuditable
{
    public long Id { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public sealed class Project : AuditableEntity
{
    public long? WorkspaceId { get; set; }
    public long OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}

public sealed class ProjectMember
{
    public long? WorkspaceId { get; set; }
    public long ProjectId { get; set; }
    public long UserId { get; set; }
    public MembershipRole Role { get; set; } = MembershipRole.Member;
    public DateTime JoinedAtUtc { get; set; }
    public Project Project { get; set; } = null!;
}

public sealed class TaskItem : AuditableEntity
{
    public long? WorkspaceId { get; set; }
    public long ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public long? AssigneeId { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public TaskState Status { get; set; } = TaskState.ToDo;
    public TaskPriority Priority { get; set; } = TaskPriority.Medium;
    public DateTime? DueDateUtc { get; set; }
    public DateTime? DeletedAtUtc { get; set; }
}

public sealed class ActivityLog
{
    public long? WorkspaceId { get; set; }
    public long Id { get; set; }
    public long ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public long ActorUserId { get; set; }
    public long? TaskId { get; set; }
    public TaskItem? Task { get; set; }
    public ActivityAction ActionType { get; set; }
    public string? Details { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum TaskState { ToDo, InProgress, Done }
public enum TaskPriority { Low, Medium, High }
public enum MembershipRole { Member }
public enum ActivityAction
{
    ProjectCreated, ProjectUpdated, ProjectDeleted, MemberAdded, MemberRemoved,
    TaskCreated, TaskUpdated, TaskAssigned, TaskUnassigned, TaskStatusChanged, TaskDeleted
}
