namespace TaskFlow.Application.Features.Dashboard;

public sealed record TaskStatusCounts(int ToDo, int InProgress, int Done);
public sealed record DashboardDto(int TotalProjects, int TotalTasks, TaskStatusCounts TasksByStatus, int AssignedToMe, int OverdueTasks);
public interface IDashboardService
{
    Task<DashboardDto> GetAsync(long? actorId, CancellationToken cancellationToken = default);
}
