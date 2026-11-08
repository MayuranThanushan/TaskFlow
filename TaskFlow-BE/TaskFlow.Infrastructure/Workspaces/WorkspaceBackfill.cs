using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Workspaces;

public sealed record WorkspaceOwnerMapping(string OwnerId, string WorkspaceId);
public sealed record WorkspaceBackfillPlan(int Version, string SnapshotId, string SourceSha256,
    string MappingSha256, WorkspaceOwnerMapping[] Owners, Dictionary<string, int> Counts);
public sealed record WorkspaceBackfillResult(string MappingSha256, int Workspaces, int Memberships, int Projects,
    int Tasks, int Activities, string AccessSha256, bool FullyScoped);

/// <summary>Offline, pre-cutover operation. This does not authorize tenant API requests.</summary>
public sealed class WorkspaceBackfill(TaskFlowDbContext database)
{
    private const int RowLimit = 100_000;
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static long ParseId(string value) => long.TryParse(value, NumberStyles.None,
        CultureInfo.InvariantCulture, out var id) && id > 0 && Id(id) == value ? id
        : throw new InvalidOperationException("Migration mapping contains an invalid identifier.");
    private static string Hash<T>(T value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private sealed record Source(Project[] Projects, ProjectMember[] Members, TaskItem[] Tasks,
        ActivityLog[] Activities, long[] Users)
    {
        public Dictionary<string, int> Counts => new()
        {
            ["users"] = Users.Length, ["projects"] = Projects.Length,
            ["deletedProjects"] = Projects.Count(p => p.DeletedAtUtc != null),
            ["projectMembers"] = Members.Length, ["tasks"] = Tasks.Length,
            ["deletedTasks"] = Tasks.Count(t => t.DeletedAtUtc != null), ["activities"] = Activities.Length
        };
        // Scope columns are deliberately excluded: the same manifest can verify/retry a backfill.
        // Private fields are hashed in memory, never included in the manifest or command output.
        public string Fingerprint => Hash(new
        {
            Users,
            Projects = Projects.Select(p => new { p.Id, p.OwnerId, p.Name, p.Description, p.CreatedAtUtc, p.UpdatedAtUtc, p.DeletedAtUtc }),
            Members = Members.Select(m => new { m.ProjectId, m.UserId, m.Role, m.JoinedAtUtc }),
            Tasks = Tasks.Select(t => new { t.Id, t.ProjectId, t.AssigneeId, t.Title, t.Description, t.Status, t.Priority, t.DueDateUtc, t.CreatedAtUtc, t.UpdatedAtUtc, t.DeletedAtUtc }),
            Activities = Activities.Select(a => new { a.Id, a.ProjectId, a.ActorUserId, a.TaskId, a.ActionType, a.Details, a.CreatedAtUtc })
        });
        public (long User, long Project)[] Access => Projects.Select(p => (p.OwnerId, p.Id))
            .Concat(Members.Select(m => (m.UserId, m.ProjectId))).Distinct().OrderBy(x => x.Item1).ThenBy(x => x.Item2).ToArray();
    }

    private async Task<Source> Read(CancellationToken token)
    {
        // Bound this pilot tool explicitly; do not truncate or declare an incomplete scan clean.
        var source = new Source(
            await database.Projects.IgnoreQueryFilters().AsNoTracking().OrderBy(p => p.Id).Take(RowLimit + 1).ToArrayAsync(token),
            await database.ProjectMembers.IgnoreQueryFilters().AsNoTracking().OrderBy(m => m.ProjectId).ThenBy(m => m.UserId).Take(RowLimit + 1).ToArrayAsync(token),
            await database.Tasks.IgnoreQueryFilters().AsNoTracking().OrderBy(t => t.Id).Take(RowLimit + 1).ToArrayAsync(token),
            await database.ActivityLogs.IgnoreQueryFilters().AsNoTracking().OrderBy(a => a.Id).Take(RowLimit + 1).ToArrayAsync(token),
            await database.Users.AsNoTracking().OrderBy(u => u.Id).Select(u => u.Id).Take(RowLimit + 1).ToArrayAsync(token));
        if (source.Counts.Values.Any(count => count > RowLimit))
            throw new InvalidOperationException("Pilot backfill row limit exceeded; use a reviewed larger-data migration.");
        var users = source.Users.ToHashSet();
        var projects = source.Projects.ToDictionary(p => p.Id);
        var tasks = source.Tasks.ToDictionary(t => t.Id);
        var members = source.Members.Select(m => (m.ProjectId, m.UserId)).ToHashSet();
        if (source.Projects.Any(p => !users.Contains(p.OwnerId))
            || source.Members.Any(m => !projects.ContainsKey(m.ProjectId) || !users.Contains(m.UserId)
                || projects[m.ProjectId].OwnerId == m.UserId || m.Role != MembershipRole.Member)
            || source.Tasks.Any(t => !projects.ContainsKey(t.ProjectId) || t.AssigneeId is { } user && !users.Contains(user))
            || source.Activities.Any(a => !projects.ContainsKey(a.ProjectId) || !users.Contains(a.ActorUserId)
                || a.TaskId is { } task && (!tasks.ContainsKey(task) || tasks[task].ProjectId != a.ProjectId)))
            throw new InvalidOperationException("Legacy ownership, membership, parent or history references require reconciliation.");
        if (source.Tasks.Any(t => t.DeletedAtUtc == null && projects[t.ProjectId].DeletedAtUtc == null
            && t.AssigneeId is { } user && projects[t.ProjectId].OwnerId != user && !members.Contains((t.ProjectId, user))))
            throw new InvalidOperationException("An active assignment is not eligible; reconcile before migration.");
        return source;
    }

    public async Task<WorkspaceBackfillPlan> PlanAsync(string snapshotId, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(snapshotId) || snapshotId.Length > 100
            || snapshotId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_' and not '.'))
            throw new InvalidOperationException("Supply a bounded snapshot reference, without paths or secrets.");
        await using var transaction = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token) : null;
        var source = await Read(token);
        var workspaces = await database.Workspaces.AsNoTracking().ToArrayAsync(token);
        var used = workspaces.Select(w => w.Id).ToHashSet();
        var mappings = source.Projects.Select(p => p.OwnerId).Distinct().Order().Select(owner =>
        {
            var existing = workspaces.SingleOrDefault(w => w.LegacyOwnerId == owner);
            var id = existing?.Id ?? 0;
            while (id == 0)
            {
                var candidate = BitConverter.ToInt64(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;
                if (candidate > 0 && used.Add(candidate)) id = candidate;
            }
            return new WorkspaceOwnerMapping(Id(owner), Id(id));
        }).ToArray();
        var plan = new WorkspaceBackfillPlan(1, snapshotId, source.Fingerprint, Hash(mappings), mappings, source.Counts);
        await Validate(source, plan, requireScoped: false, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return plan;
    }

    private async Task<WorkspaceBackfillResult> Validate(Source source, WorkspaceBackfillPlan plan, bool requireScoped, CancellationToken token)
    {
        if (plan.Version != 1 || plan.SourceSha256 != source.Fingerprint || plan.MappingSha256 != Hash(plan.Owners)
            || !plan.Counts.OrderBy(x => x.Key).SequenceEqual(source.Counts.OrderBy(x => x.Key)))
            throw new InvalidOperationException("Manifest or source changed; stop writes and review a new plan.");
        var map = plan.Owners.ToDictionary(m => ParseId(m.OwnerId), m => ParseId(m.WorkspaceId));
        if (!source.Projects.Select(p => p.OwnerId).Distinct().Order().SequenceEqual(map.Keys.Order())
            || map.Values.Distinct().Count() != map.Count)
            throw new InvalidOperationException("Manifest owner mapping does not match the source.");
        var workspaces = await database.Workspaces.AsNoTracking().ToArrayAsync(token);
        foreach (var (owner, workspace) in map)
        {
            if (workspaces.Any(w => w.Id == workspace && w.LegacyOwnerId != owner
                || w.LegacyOwnerId == owner && w.Id != workspace))
                throw new InvalidOperationException("Persisted workspace mapping conflicts with the manifest.");
        }
        var projects = source.Projects.ToDictionary(p => p.Id);
        bool Scope(long? scope, long project) => scope == map[projects[project].OwnerId] || !requireScoped && scope is null;
        if (source.Projects.Any(p => !Scope(p.WorkspaceId, p.Id)) || source.Tasks.Any(t => !Scope(t.WorkspaceId, t.ProjectId))
            || source.Members.Any(m => !Scope(m.WorkspaceId, m.ProjectId)) || source.Activities.Any(a => !Scope(a.WorkspaceId, a.ProjectId)))
            throw new InvalidOperationException("Missing or inconsistent workspace scope requires reconciliation.");
        var expected = new Dictionary<(long Workspace, long User), WorkspaceRole>();
        foreach (var (owner, workspace) in map) expected[(workspace, owner)] = WorkspaceRole.Owner;
        foreach (var member in source.Members)
        {
            var key = (map[projects[member.ProjectId].OwnerId], member.UserId);
            expected.TryAdd(key, WorkspaceRole.Guest);
        }
        var relevant = map.Values.ToHashSet();
        var memberships = (await database.WorkspaceMemberships.AsNoTracking().ToArrayAsync(token))
            .Where(m => relevant.Contains(m.WorkspaceId)).ToArray();
        if (memberships.Any(m => !expected.TryGetValue((m.WorkspaceId, m.UserId), out var role)
            || role != m.Role || m.State != WorkspaceMembershipState.Active)
            || requireScoped && (memberships.Length != expected.Count || workspaces.Count(w => relevant.Contains(w.Id)) != map.Count))
            throw new InvalidOperationException("Workspace grants differ from the legacy access mapping; reconcile explicitly.");
        // Project ownership/membership still determines private project readership. Workspace Guests
        // do not acquire other projects; historical actors never create membership grants.
        var membershipKeys = memberships.Select(m => (m.WorkspaceId, m.UserId)).ToHashSet();
        var scopedAccess = source.Access.Where(edge => membershipKeys.Contains((map[projects[edge.Project].OwnerId], edge.User))).ToArray();
        string AccessHash((long User, long Project)[] edges) => Hash(edges.Select(e => new { UserId = Id(e.User), ProjectId = Id(e.Project) }));
        if (requireScoped && AccessHash(source.Access) != AccessHash(scopedAccess))
            throw new InvalidOperationException("Authorized project access sets changed during backfill.");
        return new(plan.MappingSha256, map.Count, expected.Count, source.Projects.Length, source.Tasks.Length,
            source.Activities.Length, AccessHash(source.Access), requireScoped);
    }

    public async Task<WorkspaceBackfillResult> VerifyAsync(WorkspaceBackfillPlan plan, CancellationToken token = default)
    {
        await using var transaction = database.Database.CurrentTransaction is null
            ? await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token) : null;
        var result = await Validate(await Read(token), plan, requireScoped: true, token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return result;
    }

    public async Task<WorkspaceBackfillResult> ApplyAsync(WorkspaceBackfillPlan plan, bool maintenanceConfirmed, CancellationToken token = default)
    {
        if (!maintenanceConfirmed) throw new InvalidOperationException("Stop all writers and confirm maintenance before applying a backfill.");
        if (database.ChangeTracker.HasChanges()) throw new InvalidOperationException("Use a context without pending application changes.");
        await database.Database.OpenConnectionAsync(token);
        var name = "taskflow.workspace." + Hash(database.Database.GetDbConnection().Database)[..32];
        var locked = false;
        try
        {
            await using (var command = database.Database.GetDbConnection().CreateCommand())
            {
                command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = "SELECT GET_LOCK(@name, 0)";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = name;
                command.Parameters.Add(parameter);
                locked = Convert.ToInt32(await command.ExecuteScalarAsync(token), CultureInfo.InvariantCulture) == 1;
            }
            if (!locked) throw new InvalidOperationException("Another workspace backfill owns the migration lock.");
            await using var owned = database.Database.CurrentTransaction is null
                ? await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, token) : null;
            var transaction = owned ?? database.Database.CurrentTransaction!;
            if (transaction.GetDbTransaction().IsolationLevel != IsolationLevel.Serializable)
                throw new InvalidOperationException("Backfill requires serializable isolation.");
            await transaction.CreateSavepointAsync("workspace_backfill", token);
            try
            {
                var source = await Read(token);
                await Validate(source, plan, requireScoped: false, token);
                var workspaces = await database.Workspaces.AsNoTracking().Select(w => w.Id).ToListAsync(token);
                foreach (var mapping in plan.Owners)
                {
                    var id = ParseId(mapping.WorkspaceId);
                    if (!workspaces.Contains(id)) database.Workspaces.Add(new Workspace
                        { Id = id, Name = "Legacy workspace", LegacyOwnerId = ParseId(mapping.OwnerId) });
                }
                await database.SaveChangesAsync(token);
                var byOwner = plan.Owners.ToDictionary(m => ParseId(m.OwnerId), m => ParseId(m.WorkspaceId));
                var ownerByProject = source.Projects.ToDictionary(p => p.Id, p => p.OwnerId);
                var expected = byOwner.Select(m => (Workspace: m.Value, User: m.Key, Role: WorkspaceRole.Owner))
                    .Concat(source.Members.Select(m => (Workspace: byOwner[ownerByProject[m.ProjectId]], User: m.UserId, Role: WorkspaceRole.Guest)))
                    .GroupBy(m => (m.Workspace, m.User)).Select(g => g.OrderBy(m => m.Role == WorkspaceRole.Owner ? 0 : 1).First());
                var existing = (await database.WorkspaceMemberships.AsNoTracking().ToArrayAsync(token)).Select(m => (m.WorkspaceId, m.UserId)).ToHashSet();
                foreach (var membership in expected)
                    if (!existing.Contains((membership.Workspace, membership.User))) database.WorkspaceMemberships.Add(new WorkspaceMembership
                        { WorkspaceId = membership.Workspace, UserId = membership.User, Role = membership.Role });
                await database.SaveChangesAsync(token);
                // Controlled migration-only updates preserve audit rows, IDs and timestamps.
                await database.Database.ExecuteSqlRawAsync("UPDATE `Projects` p JOIN `Workspaces` w ON w.`LegacyOwnerId` = p.`OwnerId` SET p.`WorkspaceId` = w.`Id` WHERE p.`WorkspaceId` IS NULL", token);
                foreach (var sql in new[]
                {
                    "UPDATE `ProjectMembers` c JOIN `Projects` p ON p.`Id` = c.`ProjectId` SET c.`WorkspaceId` = p.`WorkspaceId` WHERE c.`WorkspaceId` IS NULL",
                    "UPDATE `Tasks` c JOIN `Projects` p ON p.`Id` = c.`ProjectId` SET c.`WorkspaceId` = p.`WorkspaceId` WHERE c.`WorkspaceId` IS NULL",
                    "UPDATE `ActivityLogs` c JOIN `Projects` p ON p.`Id` = c.`ProjectId` SET c.`WorkspaceId` = p.`WorkspaceId` WHERE c.`WorkspaceId` IS NULL"
                }) await database.Database.ExecuteSqlRawAsync(sql, token);
                var result = await Validate(await Read(token), plan, requireScoped: true, token);
                await transaction.ReleaseSavepointAsync("workspace_backfill", token);
                if (owned is not null) await owned.CommitAsync(token);
                return result;
            }
            catch
            {
                await transaction.RollbackToSavepointAsync("workspace_backfill", CancellationToken.None);
                database.ChangeTracker.Clear();
                throw;
            }
        }
        finally
        {
            if (locked)
            {
                await using var command = database.Database.GetDbConnection().CreateCommand();
                command.Transaction = database.Database.CurrentTransaction?.GetDbTransaction();
                command.CommandText = "SELECT RELEASE_LOCK(@name)";
                var parameter = command.CreateParameter(); parameter.ParameterName = "@name"; parameter.Value = name;
                command.Parameters.Add(parameter);
                await command.ExecuteScalarAsync(CancellationToken.None);
            }
            await database.Database.CloseConnectionAsync();
        }
    }
}
