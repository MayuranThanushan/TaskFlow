using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Workspaces;

public sealed class WorkspaceAuditConsole(TaskFlowDbContext db, IWorkspaceReads admission) : IWorkspaceAuditConsole
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    public async Task<AuditConsolePage> ReadAsync(WorkspaceReadContext context, AuditConsoleOptions options, int page, int size,
        long? highWatermark = null, CancellationToken token = default, bool forExport = false)
    {
        if (page < 1 || size is < 1 or > 1001 || (long)(page - 1) * size > 10000 || highWatermark < 0)
            throw new TaskValidationException("page", "Audit page exceeds its bound; narrow the filters.");
        Validate(options);
        var impossible = false;
        if (options.FromUtc is { } lower)
        {
            var extra = (10 - lower.Ticks % 10) % 10;
            impossible = lower.Ticks > DateTime.MaxValue.Ticks - extra;
            if (!impossible) options = options with { FromUtc = new DateTime(lower.Ticks + extra, DateTimeKind.Utc) };
        }
        if (options.ToUtc is { } upper) options = options with { ToUtc = new DateTime(upper.Ticks - upper.Ticks % 10, DateTimeKind.Utc) };
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        await admission.AuthorizeAsync(context, token);
        if (!await db.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == context.WorkspaceId && m.UserId == context.ActorId
            && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Admin), token))
            throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
        var exportsEnabled = await db.Workspaces.Where(w => w.Id == context.WorkspaceId).Select(w => w.AuditExportsEnabled).SingleAsync(token);
        if (forExport && !exportsEnabled)
            throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
        AuditConsoleRowDto[] rows; int count; long watermark;
        if (options.Source == "Governance")
        {
            var query = db.WorkspaceAuditLogs.AsNoTracking().Where(a => a.WorkspaceId == context.WorkspaceId);
            if (impossible) query = query.Where(a => false);
            if (options.ActorId is { } actor) query = query.Where(a => a.ActorUserId == actor);
            if (options.Action is { } action) query = query.Where(a => a.Action == action);
            if (options.EntityType is { } type) query = query.Where(a => a.SchemaVersion == 1 && a.SubjectType == type);
            if (options.EntityId is { } subject) query = query.Where(a => a.SchemaVersion == 1 && a.SubjectId == subject);
            if (options.FromUtc is { } from) query = query.Where(a => a.CreatedAtUtc >= from);
            if (options.ToUtc is { } to) query = query.Where(a => a.CreatedAtUtc <= to);
            if (options.Outcome == "LegacyUnknown") query = query.Where(a => a.SchemaVersion == 0);
            else if (options.Outcome is { } outcome) query = query.Where(a => a.SchemaVersion == 1 && a.Outcome == outcome);
            if (highWatermark is { } max) query = query.Where(a => a.Id <= max);
            watermark = await query.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
            count = await query.CountAsync(token);
            var data = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(token);
            rows = data.Select(a => new AuditConsoleRowDto(Id(a.Id), options.Source, Id(a.ActorUserId), a.ProjectId is { } p ? Id(p) : null,
                a.Action, a.CreatedAtUtc, null, AuditMetadata.Map(a))).ToArray();
        }
        else
        {
            // IgnoreQueryFilters propagates through subqueries: every scope and grant is explicit.
            // Deletion does not revoke a grant; revocation/suspension immediately removes history access.
            var projects = db.Projects.IgnoreQueryFilters().AsNoTracking().Where(p => p.WorkspaceId == context.WorkspaceId
                && db.WorkspaceMemberships.Any(m => m.WorkspaceId == context.WorkspaceId && m.UserId == p.OwnerId)
                && (p.OwnerId == context.ActorId || db.ProjectMembers.Any(m => m.WorkspaceId == context.WorkspaceId
                    && m.ProjectId == p.Id && m.UserId == context.ActorId && m.RevokedAtUtc == null)));
            if (options.ResourceState == "Deleted") projects = projects.Where(p => p.DeletedAtUtc != null);
            if (options.ResourceState == "Active") projects = projects.Where(p => p.DeletedAtUtc == null);
            var query = db.ActivityLogs.IgnoreQueryFilters().AsNoTracking().Where(a => a.WorkspaceId == context.WorkspaceId
                && projects.Any(p => p.Id == a.ProjectId)
                && (a.TaskId == null || db.Tasks.IgnoreQueryFilters().Any(t => t.Id == a.TaskId && t.WorkspaceId == context.WorkspaceId && t.ProjectId == a.ProjectId)));
            if (impossible) query = query.Where(a => false);
            if (options.ActorId is { } actor) query = query.Where(a => a.ActorUserId == actor);
            if (options.Action is { } action)
            {
                if (Enum.TryParse<ActivityAction>(action, out var parsed) && Enum.IsDefined(parsed) && parsed.ToString() == action) query = query.Where(a => a.ActionType == parsed);
                else query = query.Where(a => false);
            }
            if (options.EntityType is { } type) query = query.Where(a => a.SchemaVersion == 1 && a.SubjectType == type);
            if (options.EntityId is { } subject) query = query.Where(a => a.SchemaVersion == 1 && a.SubjectId == subject);
            if (options.FromUtc is { } from) query = query.Where(a => a.CreatedAtUtc >= from);
            if (options.ToUtc is { } to) query = query.Where(a => a.CreatedAtUtc <= to);
            if (options.Outcome == "LegacyUnknown") query = query.Where(a => a.SchemaVersion == 0);
            else if (options.Outcome is { } outcome) query = query.Where(a => a.SchemaVersion == 1 && a.Outcome == outcome);
            if (highWatermark is { } max) query = query.Where(a => a.Id <= max);
            watermark = await query.Select(a => (long?)a.Id).MaxAsync(token) ?? 0;
            count = await query.CountAsync(token);
            var data = await query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).Skip((page - 1) * size).Take(size)
                .Select(a => new { Event = a, Deleted = db.Projects.IgnoreQueryFilters().Any(p => p.WorkspaceId == context.WorkspaceId && p.Id == a.ProjectId && p.DeletedAtUtc != null) }).ToArrayAsync(token);
            rows = data.Select(r => new AuditConsoleRowDto(Id(r.Event.Id), options.Source, Id(r.Event.ActorUserId), Id(r.Event.ProjectId),
                r.Event.ActionType.ToString(), r.Event.CreatedAtUtc, r.Deleted, AuditMetadata.Map(r.Event))).ToArray();
        }
        await snapshot.CommitAsync(token);
        return new(rows, page, size, count, (int)Math.Ceiling(count / (double)size), Id(watermark), exportsEnabled);
    }
    public static void Validate(AuditConsoleOptions o)
    {
        if (o.Source is not ("Governance" or "Projects") || o.ActorId <= 0 || o.Action is { Length: > 80 } or ""
            || o.Action?.Any(c => !char.IsAsciiLetter(c)) == true
            || o.EntityType is not (null or "Project" or "Task" or "ProjectMembership" or "Workspace" or "WorkspaceMembership")
            || o.EntityId is { Length: > 40 } or "" || o.EntityId?.Any(c => !char.IsAsciiDigit(c) && c != ':') == true
            || o.Outcome is not (null or "Succeeded" or "LegacyUnknown")
            || o.ResourceState is not ("All" or "Active" or "Deleted") || o.Source == "Governance" && o.ResourceState != "All"
            || o.FromUtc > o.ToUtc || o.FromUtc is { Year: < 1000 } || o.ToUtc is { Year: < 1000 }
            || o.FromUtc is { Kind: not DateTimeKind.Utc } || o.ToUtc is { Kind: not DateTimeKind.Utc })
            throw new TaskValidationException("filters", "Choose supported audit filters and an ordered timestamp range.");
    }
}
