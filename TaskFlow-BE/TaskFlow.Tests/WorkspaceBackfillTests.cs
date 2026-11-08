using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MySql.Data.MySqlClient;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Infrastructure.Workspaces;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

[Collection(MySqlCollection.Name)]
public sealed class WorkspaceBackfillTests(MySqlFixture fixture)
{
    private static async Task<ApplicationUser> User(TaskFlowDbContext database)
    {
        var email = Guid.NewGuid().ToString("N") + "@example.test";
        var user = new ApplicationUser { DisplayName = "Private migration canary", Email = email, NormalizedEmail = email.ToUpperInvariant() };
        database.Users.Add(user); await database.SaveChangesAsync(); return user;
    }

    private static async Task<Project> CreateProject(TaskFlowDbContext database, ApplicationUser owner, bool deleted = false)
    {
        var project = new Project { OwnerId = owner.Id, Name = "Private project canary", DeletedAtUtc = deleted ? DateTime.UtcNow : null };
        database.Projects.Add(project); await database.SaveChangesAsync(); return project;
    }

    [MySqlFact]
    public async Task BackfillPreservesPrivateAccessDeletedHistoryGlobalAccountsAndStableRetryMappings()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var ownerA = await User(database); var ownerB = await User(database); var deletedOwner = await User(database);
        var historical = await User(database); var orphan = await User(database);
        var a = await CreateProject(database, ownerA); var privateA = await CreateProject(database, ownerA);
        var b = await CreateProject(database, ownerB); var deleted = await CreateProject(database, deletedOwner, deleted: true);
        database.ProjectMembers.AddRange(new ProjectMember { ProjectId = a.Id, UserId = ownerB.Id },
            new ProjectMember { ProjectId = b.Id, UserId = ownerA.Id });
        var task = new TaskItem { ProjectId = a.Id, Title = "Retained task canary", AssigneeId = historical.Id, DeletedAtUtc = DateTime.UtcNow };
        database.Tasks.Add(task); await database.SaveChangesAsync();
        database.ActivityLogs.Add(new ActivityLog { ProjectId = a.Id, TaskId = task.Id, ActorUserId = historical.Id, ActionType = ActivityAction.TaskDeleted, Details = "Private history canary" });
        await database.SaveChangesAsync();
        await database.Entry(a).ReloadAsync();
        var created = a.CreatedAtUtc; var updated = a.UpdatedAtUtc;
        var backfill = new WorkspaceBackfill(database);
        var plan = await backfill.PlanAsync("test-snapshot-1");
        Assert.False(await database.Workspaces.AnyAsync(w => w.LegacyOwnerId == ownerA.Id));
        var serialized = System.Text.Json.JsonSerializer.Serialize(plan);
        Assert.DoesNotContain("canary", serialized); Assert.DoesNotContain("example.test", serialized);
        Assert.DoesNotContain(plan.Owners, m => m.OwnerId == orphan.Id.ToString() || m.OwnerId == historical.Id.ToString());
        var result = await backfill.ApplyAsync(plan, true);
        Assert.True(result.FullyScoped);
        Assert.Equal(result, await backfill.ApplyAsync(plan, true));
        Assert.Equal(result, await backfill.VerifyAsync(plan));
        var workspaceA = long.Parse(plan.Owners.Single(m => m.OwnerId == ownerA.Id.ToString()).WorkspaceId);
        var workspaceB = long.Parse(plan.Owners.Single(m => m.OwnerId == ownerB.Id.ToString()).WorkspaceId);
        Assert.NotEqual(workspaceA, workspaceB);
        Assert.Equal(WorkspaceRole.Owner, (await database.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == workspaceA && m.UserId == ownerA.Id)).Role);
        Assert.Equal(WorkspaceRole.Guest, (await database.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == workspaceA && m.UserId == ownerB.Id)).Role);
        Assert.False(await database.WorkspaceMemberships.AnyAsync(m => m.UserId == orphan.Id || m.UserId == historical.Id));
        Assert.False(await database.ProjectMembers.AnyAsync(m => m.ProjectId == privateA.Id && m.UserId == ownerB.Id));
        Assert.Equal(workspaceA, (await database.Tasks.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == task.Id)).WorkspaceId);
        Assert.Equal(workspaceA, (await database.ActivityLogs.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.TaskId == task.Id)).WorkspaceId);
        Assert.NotNull((await database.Projects.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.Id == deleted.Id)).WorkspaceId);
        var unchanged = await database.Projects.AsNoTracking().SingleAsync(p => p.Id == a.Id);
        Assert.Equal(created, unchanged.CreatedAtUtc); Assert.Equal(updated, unchanged.UpdatedAtUtc); Assert.Equal(ownerA.Id, unchanged.OwnerId);
        // The expansion still permits current v1 writers. A previous plan must never certify new unscoped data.
        await CreateProject(database, ownerA);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.VerifyAsync(plan));
        var next = await backfill.PlanAsync("test-snapshot-2");
        Assert.Equal(workspaceA.ToString(), next.Owners.Single(m => m.OwnerId == ownerA.Id.ToString()).WorkspaceId);
        Assert.True((await backfill.ApplyAsync(next, true)).FullyScoped);
    }

    [MySqlFact]
    public async Task StaleOrTamperedPlanAndMissingMaintenanceNeverChangeMapping()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var owner = await User(database); var project = await CreateProject(database, owner);
        var backfill = new WorkspaceBackfill(database); var plan = await backfill.PlanAsync("test-snapshot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.ApplyAsync(plan, false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.ApplyAsync(plan with { MappingSha256 = "changed" }, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.VerifyAsync(plan));
        project = await database.Projects.SingleAsync(p => p.Id == project.Id);
        project.Name = "Changed after snapshot"; await database.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.ApplyAsync(plan, true));
        Assert.False(await database.Workspaces.AnyAsync(w => w.LegacyOwnerId == owner.Id));
        Assert.Null((await database.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id)).WorkspaceId);
    }

    [MySqlFact]
    public async Task AmbiguousOwnerMembershipAndIneligibleActiveAssignmentRequireReconciliation()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var owner = await User(database); var outsider = await User(database); var project = await CreateProject(database, owner);
        var member = new ProjectMember { ProjectId = project.Id, UserId = owner.Id };
        database.ProjectMembers.Add(member); await database.SaveChangesAsync();
        var backfill = new WorkspaceBackfill(database);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.PlanAsync("snapshot"));
        database.ProjectMembers.Remove(member); await database.SaveChangesAsync();
        var task = new TaskItem { ProjectId = project.Id, Title = "Unresolved assignment", AssigneeId = outsider.Id };
        database.Tasks.Add(task); await database.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.PlanAsync("snapshot"));
        task.DeletedAtUtc = DateTime.UtcNow; await database.SaveChangesAsync();
        await backfill.PlanAsync("snapshot"); // Historical assignment is retained, not converted into a grant.
    }

    [MySqlFact]
    public async Task ExistingConflictingScopeIsNeverOverwritten()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var owner = await User(database); var project = await CreateProject(database, owner);
        var backfill = new WorkspaceBackfill(database); var plan = await backfill.PlanAsync("snapshot");
        var unrelated = new Workspace { Name = "Separate company" };
        database.Workspaces.Add(unrelated); await database.SaveChangesAsync();
        database.WorkspaceMemberships.Add(new WorkspaceMembership { WorkspaceId = unrelated.Id, UserId = owner.Id, Role = WorkspaceRole.Owner });
        await database.SaveChangesAsync();
        project.WorkspaceId = unrelated.Id; await database.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.ApplyAsync(plan, true));
        Assert.Equal(unrelated.Id, (await database.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id)).WorkspaceId);
    }

    private sealed class FailMembershipSave : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<WorkspaceMembership>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Injected failure after workspace insertion");
            return ValueTask.FromResult(result);
        }
    }

    [MySqlFact]
    public async Task FailedBackfillRollsBackGeneratedRowsAndTheSameManifestCanRetry()
    {
        var failure = new FailMembershipSave();
        var options = new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(failure).Options;
        await using var database = new TaskFlowDbContext(options, TimeProvider.System);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var owner = await User(database); var project = await CreateProject(database, owner);
        var backfill = new WorkspaceBackfill(database); var plan = await backfill.PlanAsync("snapshot");
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.ApplyAsync(plan, true));
        Assert.False(await database.Workspaces.AnyAsync(w => w.LegacyOwnerId == owner.Id));
        Assert.Null((await database.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id)).WorkspaceId);
        failure.Enabled = false;
        Assert.True((await backfill.ApplyAsync(plan, true)).FullyScoped);
    }

    [MySqlFact]
    public async Task ExistingExtraWorkspaceGrantsAreRejectedInsteadOfSilentlyRemoved()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var owner = await User(database); var outsider = await User(database); var project = await CreateProject(database, owner);
        var backfill = new WorkspaceBackfill(database); var plan = await backfill.PlanAsync("snapshot");
        var id = long.Parse(plan.Owners.Single(m => m.OwnerId == owner.Id.ToString()).WorkspaceId);
        database.Workspaces.Add(new Workspace { Id = id, Name = "Legacy workspace", LegacyOwnerId = owner.Id });
        database.WorkspaceMemberships.Add(new WorkspaceMembership { WorkspaceId = id, UserId = outsider.Id, Role = WorkspaceRole.Guest });
        await database.SaveChangesAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.ApplyAsync(plan, true));
        Assert.True(await database.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == id && m.UserId == outsider.Id));
        Assert.Null((await database.Projects.AsNoTracking().SingleAsync(p => p.Id == project.Id)).WorkspaceId);
    }

    [MySqlFact]
    public async Task CompetingMigrationLeaseRefusesWritesAndCanRetryAfterRelease()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var owner = await User(database); await CreateProject(database, owner);
        var backfill = new WorkspaceBackfill(database); var plan = await backfill.PlanAsync("snapshot");
        await using var competing = new MySqlConnection(fixture.ConnectionString);
        await competing.OpenAsync();
        await using var lease = competing.CreateCommand();
        lease.CommandText = "SELECT GET_LOCK(CONCAT('taskflow.workspace.', LEFT(LOWER(SHA2(JSON_QUOTE(DATABASE()), 256)), 32)), 0)";
        Assert.Equal(1, Convert.ToInt32(await lease.ExecuteScalarAsync()));
        try
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => backfill.ApplyAsync(plan, true));
            Assert.Contains("migration lock", error.Message);
            Assert.False(await database.Workspaces.AnyAsync(w => w.LegacyOwnerId == owner.Id));
        }
        finally
        {
            lease.CommandText = "SELECT RELEASE_LOCK(CONCAT('taskflow.workspace.', LEFT(LOWER(SHA2(JSON_QUOTE(DATABASE()), 256)), 32)))";
            await lease.ExecuteScalarAsync();
        }
        Assert.True((await backfill.ApplyAsync(plan, true)).FullyScoped);
    }

    [MySqlFact]
    public async Task OfflineCommandUsesExclusiveManifestsAndRejectsIgnoredOrMissingMaintenanceOptions()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var owner = await User(database); await CreateProject(database, owner);
        var directory = Path.Combine(Path.GetTempPath(), "taskflow-workspace-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var manifest = "--manifest=" + Path.Combine(directory, "plan.json");
        Task<int> Command(params string[] args) => TaskFlow.Api.Operations.WorkspaceMigrationCommand.ExecuteAsync(args, database);
        try
        {
            Assert.Equal(0, await Command("--workspace-backfill=plan", manifest, "--snapshot-id=verified-test-snapshot"));
            Assert.Equal(1, await Command("--workspace-backfill=plan", manifest, "--snapshot-id=verified-test-snapshot"));
            Assert.Equal(1, await Command("--workspace-backfill=apply", manifest));
            Assert.Equal(1, await Command("--workspace-backfill=apply", manifest, "--maintenance-confirmed=true", "--snapshot-id=ignored"));
            Assert.False(await database.Workspaces.AnyAsync(w => w.LegacyOwnerId == owner.Id));
            Assert.Equal(0, await Command("--workspace-backfill=apply", manifest, "--maintenance-confirmed=true"));
            Assert.Equal(0, await Command("--workspace-backfill=verify", manifest));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [MySqlFact]
    public async Task ExpansionKeepsNullableScopeAndRejectsDanglingWorkspaceReferences()
    {
        await using var database = fixture.CreateContext();
        await using var transaction = await database.Database.BeginTransactionAsync();
        var owner = await User(database); var project = await CreateProject(database, owner);
        Assert.True(database.Model.FindEntityType(typeof(Project))!.FindProperty(nameof(Project.WorkspaceId))!.IsNullable);
        Assert.True(database.Model.FindEntityType(typeof(TaskItem))!.FindProperty(nameof(TaskItem.WorkspaceId))!.IsNullable);
        Assert.Null(project.WorkspaceId);
        project.WorkspaceId = long.MaxValue;
        await Assert.ThrowsAsync<DbUpdateException>(() => database.SaveChangesAsync());
    }
}
