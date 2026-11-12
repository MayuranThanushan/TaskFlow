namespace TaskFlow.Domain.Entities;

public sealed class NotificationPreference : IVersionedResource
{
    public long WorkspaceId { get; set; }
    public long UserId { get; set; }
    public bool InAppEnabled { get; set; } = true;
    public bool TaskAssignedEnabled { get; set; } = true;
    public long Version { get; set; } = 1;
}
