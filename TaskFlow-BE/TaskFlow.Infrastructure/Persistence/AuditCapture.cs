using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Persistence;

public static class AuditCorrelation
{
    private static readonly AsyncLocal<string?> Ambient = new();
    public static string Current() => Ambient.Value ?? (Activity.Current is { IdFormat: ActivityIdFormat.W3C, Id: { } id } ? id : Guid.NewGuid().ToString("N"));
    public static IDisposable Begin(string value)
    {
        if (value.Length is not (32 or 55) || value.Any(c => !char.IsAsciiHexDigit(c) && c != '-')) throw new ArgumentException("Invalid audit correlation.");
        var prior = Ambient.Value; Ambient.Value = value; return new Reset(prior);
    }
    private sealed class Reset(string? prior) : IDisposable { public void Dispose() => Ambient.Value = prior; }
}

// One context/transaction's pending deltas. Private text never enters a stored snapshot.
internal sealed class AuditCapture
{
    private sealed record Change(object Entity, AuditChangeDto Delta);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<Change> pending = [];
    private readonly string correlation = AuditCorrelation.Current();
    private static string? Scalar(object? value) => value switch
    {
        null => null, DateTime date => date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
        Enum e => e.ToString(), long n => n.ToString(CultureInfo.InvariantCulture), int n => n.ToString(CultureInfo.InvariantCulture), bool b => b ? "true" : "false", _ => throw new InvalidOperationException("Unsupported audit scalar.")
    };
    public void Prepare(TaskFlowDbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified).ToArray())
        {
            string[] properties = entry.Entity switch
            {
                Project => ["Name", "Description", "OwnerId", "DeletedAtUtc"],
                TaskItem => ["Title", "Description", "Status", "Priority", "AssigneeId", "DueDateUtc", "DeletedAtUtc"],
                ProjectMember => ["Role", "RevokedAtUtc"], WorkspaceMembership => ["Role", "State"], Workspace => ["Name", "AuditRetentionDays", "DeletedContentRetentionDays", "LegalHold", "AuditExportsEnabled"], _ => []
            };
            foreach (var field in properties)
            {
                var property = entry.Property(field);
                var before = entry.State == EntityState.Added ? null : property.OriginalValue;
                var after = property.CurrentValue;
                if (Equals(before, after)) continue;
                var redacted = field is "Name" or "Title" or "Description";
                var delta = new AuditChangeDto(char.ToLowerInvariant(field[0]) + field[1..],
                    redacted ? before is null ? null : "present" : Scalar(before),
                    redacted ? after is null ? null : "present" : Scalar(after), redacted);
                var index = pending.FindIndex(c => ReferenceEquals(c.Entity, entry.Entity) && c.Delta.Field == delta.Field);
                if (index < 0) pending.Add(new(entry.Entity, delta));
                else pending[index] = new(entry.Entity, delta with { Before = pending[index].Delta.Before });
            }
        }
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added && e.Entity is IVersionedAudit).ToArray())
        {
            var row = (IVersionedAudit)entry.Entity;
            row.SchemaVersion = 1; row.Outcome = "Succeeded"; row.CorrelationId = correlation;
            List<AuditChangeDto> changes;
            if (row is ActivityLog activity)
            {
                var user = User(activity.Details);
                row.SubjectType = activity.TaskId != null ? "Task" : activity.ActionType is ActivityAction.MemberAdded or ActivityAction.MemberRemoved or ActivityAction.MemberRoleChanged ? "ProjectMembership" : "Project";
                row.SubjectId = activity.TaskId?.ToString(CultureInfo.InvariantCulture) ?? (row.SubjectType == "ProjectMembership" ? activity.ProjectId.ToString(CultureInfo.InvariantCulture) + ":" + user : activity.ProjectId.ToString(CultureInfo.InvariantCulture));
                var candidates = pending.Where(c => activity.TaskId is { } task ? c.Entity is TaskItem t && t.Id == task
                    : row.SubjectType == "ProjectMembership" ? c.Entity is ProjectMember m && m.ProjectId == activity.ProjectId && m.UserId.ToString(CultureInfo.InvariantCulture) == user
                    : c.Entity is Project p && p.Id == activity.ProjectId);
                changes = candidates.Select(c => c.Delta).Where(d => activity.ActionType switch
                {
                    ActivityAction.TaskAssigned or ActivityAction.TaskUnassigned => d.Field == "assigneeId",
                    ActivityAction.TaskStatusChanged => d.Field == "status",
                    ActivityAction.TaskUpdated => d.Field is "title" or "description" or "priority" or "dueDateUtc",
                    ActivityAction.ProjectUpdated => d.Field is "name" or "description",
                    ActivityAction.TaskDeleted or ActivityAction.ProjectDeleted => d.Field == "deletedAtUtc",
                    _ => true
                }).ToList();
            }
            else if (row is WorkspaceAuditLog governance)
            {
                row.SubjectType = governance.Action is "CompanyCreated" or "CompanyPolicyChanged" ? "Workspace" : governance.ProjectId is null ? "WorkspaceMembership"
                    : governance.Action == "ProjectOwnershipTransferred" ? "Project" : "ProjectMembership";
                row.SubjectId = row.SubjectType switch
                {
                    "Workspace" => governance.WorkspaceId.ToString(CultureInfo.InvariantCulture),
                    "Project" => governance.ProjectId!.Value.ToString(CultureInfo.InvariantCulture),
                    _ => (governance.ProjectId ?? governance.WorkspaceId).ToString(CultureInfo.InvariantCulture) + ":" + governance.TargetUserId.ToString(CultureInfo.InvariantCulture)
                };
                changes = governance.Action is "CompanyCreated" or "CompanyPolicyChanged" ? pending.Where(c => c.Entity is Workspace w && w.Id == governance.WorkspaceId).Select(c => c.Delta).ToList()
                    : governance.Action == "ProjectOwnershipTransferred" ? pending.Where(c => c.Entity is Project p && p.Id == governance.ProjectId).Select(c => c.Delta).ToList()
                    : governance.FromRole is not null || governance.ToRole is not null ? [new(governance.Action.Contains("Suspended", StringComparison.Ordinal) || governance.Action.Contains("Reactivated", StringComparison.Ordinal) || governance.Action == "CompanyMemberRemoved" ? "state" : "role", governance.FromRole, governance.ToRole, false)]
                    : pending.Where(c => c.Entity is WorkspaceMembership m && m.WorkspaceId == governance.WorkspaceId && m.UserId == governance.TargetUserId).Select(c => c.Delta).ToList();
            }
            else throw new InvalidOperationException("Unsupported business audit record.");
            row.ChangesJson = JsonSerializer.Serialize(changes.OrderBy(c => c.Field, StringComparer.Ordinal).Take(16), Json);
        }
    }
    private static string? User(string? json)
    {
        if (json is null) return null;
        try { using var data = JsonDocument.Parse(json); return data.RootElement.TryGetProperty("userId", out var user) && user.ValueKind == JsonValueKind.String ? user.GetString() : null; }
        catch (JsonException) { return null; }
    }
    public void Saved(bool hadAudit) { if (hadAudit) pending.Clear(); }
}
