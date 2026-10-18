using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Projects;

public sealed class ProjectService(TaskFlowDbContext database, IProjectTransactions transactions, TimeProvider clock) : IProjectService
{
    public async Task<ProjectDto> CreateAsync(long? actorId, string name, string? description, CancellationToken cancellationToken = default)
    {
        RequireActor(actorId);
        name = TextRules.Required(name, 150, nameof(name));
        description = TextRules.Optional(description, 10_000, nameof(description));
        var project = await transactions.CreateAsync(actorId, (scope, _) =>
        {
            scope.Project.Name = name;
            scope.Project.Description = description;
            return Task.FromResult(scope.Project);
        }, cancellationToken);
        return Map(project);
    }

    public async Task<ProjectDto> UpdateAsync(long? actorId, long projectId, string name, string? description, CancellationToken cancellationToken = default)
    {
        RequireActor(actorId);
        name = TextRules.Required(name, 150, nameof(name));
        description = TextRules.Optional(description, 10_000, nameof(description));
        var project = await transactions.ExecuteAsync(actorId, projectId, ProjectOperation.ManageProject, (scope, _) =>
        {
            if (scope.Project.Name != name || scope.Project.Description != description)
            {
                scope.Project.Name = name;
                scope.Project.Description = description;
                scope.Record(ActivityAction.ProjectUpdated);
            }
            return Task.FromResult(scope.Project);
        }, cancellationToken);
        return Map(project);
    }

    public async Task DeleteAsync(long? actorId, long projectId, CancellationToken cancellationToken = default)
    {
        await transactions.ExecuteAsync(actorId, projectId, ProjectOperation.ManageProject, (scope, _) =>
        {
            scope.Project.DeletedAtUtc = clock.GetUtcNow().UtcDateTime;
            scope.Record(ActivityAction.ProjectDeleted);
            return Task.FromResult(true);
        }, cancellationToken);
    }

    public async Task<ProjectPage> ListAsync(long? actorId, ProjectListOptions options, CancellationToken cancellationToken = default)
    {
        var actor = RequireActor(actorId);
        if (options.Page < 1 || options.PageSize is < 1 or > 100 || (long)(options.Page - 1) * options.PageSize > int.MaxValue
            || options.Search?.Length > 200 || options.SortBy is not ("name" or "createdAtUtc" or "updatedAtUtc")
            || options.SortDirection is not ("asc" or "desc"))
            throw new ArgumentException("Invalid project list options.", nameof(options));
        var query = ProjectAccess.VisibleProjects(database, actor).AsNoTracking();
        var search = options.Search?.Trim();
        if (!string.IsNullOrEmpty(search)) query = query.Where(project => project.Name.Contains(search));
        // Count and page share a consistent read snapshot, even if another request creates a project.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var total = await query.CountAsync(cancellationToken);
        var ascending = options.SortDirection == "asc";
        var ordered = options.SortBy switch
        {
            "name" => ascending ? query.OrderBy(project => project.Name) : query.OrderByDescending(project => project.Name),
            "updatedAtUtc" => ascending ? query.OrderBy(project => project.UpdatedAtUtc) : query.OrderByDescending(project => project.UpdatedAtUtc),
            _ => ascending ? query.OrderBy(project => project.CreatedAtUtc) : query.OrderByDescending(project => project.CreatedAtUtc)
        };
        var projects = await ordered.ThenByDescending(project => project.Id)
            .Skip((options.Page - 1) * options.PageSize).Take(options.PageSize).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ProjectPage(projects.Select(Map).ToArray(), options.Page, options.PageSize, total,
            (int)Math.Ceiling(total / (double)options.PageSize));
    }

    public async Task<ProjectDetailsDto> GetAsync(long? actorId, long projectId, CancellationToken cancellationToken = default)
    {
        var actor = RequireActor(actorId);
        // Access and both counts are evaluated in one database statement/snapshot.
        var row = await ProjectAccess.VisibleProjects(database, actor).AsNoTracking().Where(project => project.Id == projectId)
            .Select(project => new
            {
                Project = project,
                Total = database.Tasks.Count(task => task.ProjectId == project.Id),
                Completed = database.Tasks.Count(task => task.ProjectId == project.Id && task.Status == TaskState.Done)
            }).SingleOrDefaultAsync(cancellationToken);
        if (row is null) throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        var dto = Map(row.Project);
        var percentage = row.Total == 0 ? 0 : (int)Math.Round(row.Completed * 100m / row.Total, MidpointRounding.AwayFromZero);
        return new ProjectDetailsDto(dto.Id, dto.Name, dto.Description, dto.OwnerId, dto.CreatedAtUtc, dto.UpdatedAtUtc,
            row.Total, row.Completed, percentage);
    }

    private static long RequireActor(long? actorId) => actorId is > 0 ? actorId.Value
        : throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
    private static ProjectDto Map(Project project) => new(project.Id.ToString(CultureInfo.InvariantCulture), project.Name,
        project.Description, project.OwnerId.ToString(CultureInfo.InvariantCulture), project.CreatedAtUtc, project.UpdatedAtUtc);
}
