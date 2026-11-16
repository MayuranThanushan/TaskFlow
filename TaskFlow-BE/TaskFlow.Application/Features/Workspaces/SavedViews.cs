namespace TaskFlow.Application.Features.Workspaces;

public sealed record SavedViewDefinition(string Name, string Surface, string? ProjectId, bool Shared, Dictionary<string,string> Filters);
public sealed record SavedViewDto(string Id, string UserId, string Name, string Surface, string? ProjectId, bool Shared, Dictionary<string,string> Filters, string Version, bool CanManage);
public sealed record SavedViewPage(IReadOnlyList<SavedViewDto> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record ViewPreferenceDto(string Context, string? SavedViewId, string Version, bool Available);
public interface ISavedViews
{
    Task<SavedViewPage> ViewsAsync(WorkspaceReadContext scope, int page, int pageSize, CancellationToken token);
    Task<SavedViewDto> ViewAsync(WorkspaceReadContext scope, long id, CancellationToken token);
    Task<SavedViewDto> SaveViewAsync(WorkspaceReadContext scope, long? id, string? expectedVersion, SavedViewDefinition definition, CancellationToken token);
    Task DeleteViewAsync(WorkspaceReadContext scope, long id, string expectedVersion, CancellationToken token);
    Task<ViewPreferenceDto> PreferenceAsync(WorkspaceReadContext scope, string context, CancellationToken token);
    Task<ViewPreferenceDto> SetPreferenceAsync(WorkspaceReadContext scope, string context, string expectedVersion, long? viewId, CancellationToken token);
}
