using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Projects;

public sealed class ActivityService(TaskFlowDbContext database) : IActivityService
{
    public async Task<ActivityPage> ListAsync(long? actorId, long projectId, ActivityOptions options, CancellationToken cancellationToken = default)
    {
        if (actorId is null or <= 0) throw new ProjectAccessException(ProjectAccessDecision.Unauthenticated);
        if (options.Page < 1 || options.PageSize is < 1 or > 100 || (long)(options.Page - 1) * options.PageSize > int.MaxValue || options.FromUtc > options.ToUtc)
            throw new ArgumentException("Invalid activity query.");
        await using var snapshot = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        if (!await ProjectAccess.VisibleProjects(database, actorId.Value).AnyAsync(p => p.Id == projectId, cancellationToken)) throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        // Do not join/filter by the task: individually deleted tasks retain their history.
        var query = database.ActivityLogs.AsNoTracking().Where(a => a.ProjectId == projectId);
        if (options.FromUtc is { } from)
        {
            var extra = (10 - from.Ticks % 10) % 10;
            if (from.Ticks > DateTime.MaxValue.Ticks - extra) query = query.Where(a => false);
            else { var lower = new DateTime(from.Ticks + extra, DateTimeKind.Utc); query = query.Where(a => a.CreatedAtUtc >= lower); }
        }
        if (options.ToUtc is { } to) { var upper = new DateTime(to.Ticks - to.Ticks % 10, DateTimeKind.Utc); query = query.Where(a => a.CreatedAtUtc <= upper); }
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Skip((options.Page - 1) * options.PageSize).Take(options.PageSize).ToListAsync(cancellationToken);
        await snapshot.CommitAsync(cancellationToken);
        return new(rows.Select(a => new ActivityDto(Id(a.Id), Id(a.ProjectId), Id(a.ActorUserId), a.TaskId is { } task ? Id(task) : null,
            a.ActionType.ToString(), Summary(a), a.CreatedAtUtc)).ToArray(), options.Page, options.PageSize, total, (int)Math.Ceiling(total / (double)options.PageSize));
    }
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static string? Detail(ActivityLog activity, string key)
    {
        if (activity.Details is null) return null;
        try { using var document = JsonDocument.Parse(activity.Details); return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null; }
        catch (JsonException) { return null; }
    }
    private static string User(ActivityLog activity)
    {
        var id = Detail(activity, "userId");
        return id is not null && id.All(char.IsAsciiDigit) && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 ? "user " + Id(parsed) : "a user";
    }
    private static string? Status(string? value) => value switch { "ToDo" => "To Do", "InProgress" => "In Progress", "Done" => "Completed", _ => null };
    internal static string Summary(ActivityLog activity) => activity.ActionType switch
    {
        ActivityAction.ProjectCreated => "Project created.", ActivityAction.ProjectUpdated => "Project updated.", ActivityAction.ProjectDeleted => "Project deleted.",
        ActivityAction.MemberAdded => "Added " + User(activity) + " to the project.", ActivityAction.MemberRemoved => "Removed " + User(activity) + " from the project.",
        ActivityAction.TaskCreated => "Task created.", ActivityAction.TaskUpdated => "Task updated.", ActivityAction.TaskDeleted => "Task deleted.",
        ActivityAction.TaskAssigned => "Task assigned to " + User(activity) + ".", ActivityAction.TaskUnassigned => "Unassigned " + User(activity) + " from the task.",
        ActivityAction.TaskStatusChanged => Status(Detail(activity, "from")) is { } from && Status(Detail(activity, "to")) is { } to ? "Status changed from " + from + " to " + to + "." : "Task status changed.",
        _ => "Project activity recorded."
    };
}
