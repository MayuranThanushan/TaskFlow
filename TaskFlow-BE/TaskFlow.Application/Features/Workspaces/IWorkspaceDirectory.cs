namespace TaskFlow.Application.Features.Workspaces;

public interface IWorkspaceDirectory
{
    Task<WorkspaceDto> CreateAsync(long actorId, string name, CancellationToken token = default);
}
