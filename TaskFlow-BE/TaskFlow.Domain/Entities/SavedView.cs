namespace TaskFlow.Domain.Entities;

public sealed class SavedView : IVersionedResource
{
    public long Id { get; set; }
    public long WorkspaceId { get; set; }
    public long UserId { get; set; }
    public string Name { get; set; } = "";
    public string Surface { get; set; } = "work";
    public long? ProjectId { get; set; }
    public bool Shared { get; set; }
    public string FiltersJson { get; set; } = "{}";
    public long Version { get; set; } = 1;
}
public sealed class ViewPreference : IVersionedResource
{
    public long WorkspaceId { get; set; }
    public long UserId { get; set; }
    public string Context { get; set; } = "work";
    public long? SavedViewId { get; set; }
    public long Version { get; set; } = 1;
}
