using System.Data;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

[Collection(MySqlCollection.Name)]
public sealed class ProjectFoundationTests(MySqlFixture fixture)
{
    [MySqlFact, Trait("Category", "MySql")]
    public async Task AccessUsesCurrentOwnershipAndMembershipAndHidesMissingResources()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        Assert.Equal(ProjectAccessDecision.Allowed, await rig.Access.CheckAsync(rig.Owner, rig.Project, ProjectOperation.ManageProject));
        Assert.Equal(ProjectAccessDecision.Allowed, await rig.Access.CheckAsync(rig.Member, rig.Project, ProjectOperation.WriteTasks));
        Assert.Equal(ProjectAccessDecision.Forbidden, await rig.Access.CheckAsync(rig.Member, rig.Project, ProjectOperation.ManageMembers));
        Assert.Equal(ProjectAccessDecision.NotFound, await rig.Access.CheckAsync(rig.Outsider, rig.Project, ProjectOperation.Read));
        Assert.Equal(ProjectAccessDecision.NotFound, await rig.Access.CheckAsync(rig.Owner, long.MaxValue, ProjectOperation.Read));
        await using var database = fixture.CreateContext();
        Assert.False(await database.ProjectMembers.AnyAsync(member => member.ProjectId == rig.Project && member.UserId == rig.Owner));
        database.ProjectMembers.Remove(await database.ProjectMembers.SingleAsync(member => member.ProjectId == rig.Project && member.UserId == rig.Member));
        await database.SaveChangesAsync();
        Assert.Equal(ProjectAccessDecision.NotFound, await rig.Access.CheckAsync(rig.Member, rig.Project, ProjectOperation.Read));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task DirectTasksResolveThroughActiveAccessibleParents()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        Assert.Equal(ProjectAccessDecision.Allowed, await rig.Access.CheckTaskAsync(rig.Member, rig.Task, ProjectOperation.Read));
        Assert.Equal(ProjectAccessDecision.Forbidden, await rig.Access.CheckTaskAsync(rig.Member, rig.Task, ProjectOperation.DeleteTasks));
        Assert.Equal(ProjectAccessDecision.NotFound, await rig.Access.CheckTaskAsync(rig.Owner, rig.OtherTask, ProjectOperation.Read));
        await using var database = fixture.CreateContext();
        var task = await database.Tasks.SingleAsync(task => task.Id == rig.Task);
        task.DeletedAtUtc = rig.Clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync();
        Assert.Equal(ProjectAccessDecision.NotFound, await rig.Access.CheckTaskAsync(rig.Owner, rig.Task, ProjectOperation.Read));
        var project = await database.Projects.SingleAsync(project => project.Id == rig.Project);
        project.DeletedAtUtc = rig.Clock.GetUtcNow().UtcDateTime;
        await database.SaveChangesAsync();
        foreach (var user in new[] { rig.Owner, rig.Member, rig.Outsider })
            Assert.Equal(ProjectAccessDecision.NotFound, await rig.Access.CheckAsync(user, rig.Project, ProjectOperation.Read));
        var denial = await Assert.ThrowsAsync<ProjectAccessException>(() => rig.Transactions.ExecuteAsync(rig.Owner,
            rig.Project, ProjectOperation.ManageProject, (_, _) => Task.FromResult(0)));
        Assert.Equal(ProjectAccessDecision.NotFound, denial.Decision);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task CreationCommitsGeneratedIdsAuthoritativeActorAndActivityTogether()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        var created = await rig.Transactions.CreateAsync(rig.Owner, (scope, _) =>
        {
            scope.Project.Name = " New project ";
            return Task.FromResult(scope.Project);
        });
        Assert.True(created.Id > 0);
        await using var database = fixture.CreateContext();
        var project = await database.Projects.SingleAsync(project => project.Id == created.Id);
        var activity = await database.ActivityLogs.SingleAsync(activity => activity.ProjectId == project.Id);
        Assert.Equal("New project", project.Name);
        Assert.Equal(rig.Owner, project.OwnerId);
        Assert.Equal(rig.Owner, activity.ActorUserId);
        Assert.Equal(ActivityAction.ProjectCreated, activity.ActionType);
        Assert.Equal(rig.Clock.GetUtcNow().UtcDateTime, activity.CreatedAtUtc);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task UpdatesPersistStructuredDetailsAndAssigneeEligibilityUsesTheSameBoundary()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(1);
        await rig.Transactions.ExecuteAsync(rig.Owner, rig.Project, ProjectOperation.ManageProject, async (scope, token) =>
        {
            Assert.True(await scope.IsEligibleAssigneeAsync(rig.Owner, token));
            Assert.True(await scope.IsEligibleAssigneeAsync(rig.Member, token));
            Assert.False(await scope.IsEligibleAssigneeAsync(rig.Outsider, token));
            scope.Project.Name = "Updated";
            var details = new Dictionary<string, string?> { ["name"] = "Updated" };
            scope.Record(ActivityAction.ProjectUpdated, details);
            details["name"] = "Changed after recording";
            return 0;
        });
        await using var database = fixture.CreateContext();
        var activity = await database.ActivityLogs.SingleAsync(activity => activity.ProjectId == rig.Project);
        Assert.Equal("Updated", JsonDocument.Parse(activity.Details!).RootElement.GetProperty("name").GetString());
        Assert.Equal(rig.Clock.GetUtcNow().UtcDateTime, (await database.Projects.SingleAsync(project => project.Id == rig.Project)).UpdatedAtUtc);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivityForeignKeyFailureRollsBackAlreadySavedMutationAndCreation()
    {
        var failure = new CorruptActivityActor();
        await using var rig = await Rig.CreateAsync(fixture, failure);
        await using var database = fixture.CreateContext();
        var before = await database.Projects.AsNoTracking().SingleAsync(project => project.Id == rig.Project);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(1);
        failure.Enabled = true;
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => rig.Transactions.ExecuteAsync(rig.Owner, rig.Project,
            ProjectOperation.ManageProject, (scope, _) =>
            {
                scope.Project.Name = "Must roll back";
                scope.Record(ActivityAction.ProjectUpdated);
                return Task.FromResult(0);
            }));
        Assert.Equal(1452, Assert.IsType<MySqlException>(error.InnerException).Number);
        Assert.True(failure.SawPreviouslySavedProject);
        var after = await database.Projects.AsNoTracking().SingleAsync(project => project.Id == rig.Project);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        await Assert.ThrowsAsync<DbUpdateException>(() => rig.Transactions.CreateAsync(rig.Owner, (scope, _) =>
        {
            scope.Project.Name = "Failed creation";
            return Task.FromResult(scope.Project);
        }));
        Assert.False(await database.Projects.AnyAsync(project => project.OwnerId == rig.Owner && project.Name == "Failed creation"));
        Assert.False(await database.ActivityLogs.AnyAsync(activity => activity.ProjectId == rig.Project));
        failure.Enabled = false;
        await rig.Transactions.ExecuteAsync(rig.Owner, rig.Project, ProjectOperation.ManageProject, (scope, _) =>
        {
            scope.Project.Name = "Recovered";
            scope.Record(ActivityAction.ProjectUpdated);
            return Task.FromResult(0);
        });
        Assert.Equal("Recovered", (await database.Projects.AsNoTracking().SingleAsync(project => project.Id == rig.Project)).Name);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task NoOpDoesNotChangeTimestampOrAppendHistoryAndMissingActivityIsRejected()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        await using var database = fixture.CreateContext();
        var before = await database.Projects.AsNoTracking().SingleAsync(project => project.Id == rig.Project);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(1);
        await rig.Transactions.ExecuteAsync(rig.Owner, rig.Project, ProjectOperation.ManageProject, (scope, _) =>
        {
            scope.Project.Name = " " + scope.Project.Name + " ";
            return Task.FromResult(0);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Transactions.ExecuteAsync(rig.Owner, rig.Project,
            ProjectOperation.ManageProject, (scope, _) =>
            {
                scope.Record(ActivityAction.ProjectUpdated);
                return Task.FromResult(0);
            }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Transactions.ExecuteAsync(rig.Owner, rig.Project,
            ProjectOperation.ManageProject, (scope, _) =>
            {
                scope.Project.Name = "Missing history";
                return Task.FromResult(0);
            }));
        var after = await database.Projects.AsNoTracking().SingleAsync(project => project.Id == rig.Project);
        Assert.Equal(before.Name, after.Name);
        Assert.Equal(before.UpdatedAtUtc, after.UpdatedAtUtc);
        Assert.False(await database.ActivityLogs.AnyAsync(activity => activity.ProjectId == rig.Project));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task DeniedOperationsNeverRunCallbacksAndTaskAccessCannotEditProjectMetadata()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        foreach (var entry in new[] { (rig.Outsider, ProjectAccessDecision.NotFound), (rig.Member, ProjectAccessDecision.Forbidden) })
        {
            var called = false;
            var error = await Assert.ThrowsAsync<ProjectAccessException>(() => rig.Transactions.ExecuteAsync(entry.Item1,
                rig.Project, ProjectOperation.ManageProject, (_, _) => { called = true; return Task.FromResult(0); }));
            Assert.Equal(entry.Item2, error.Decision);
            Assert.False(called);
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Transactions.ExecuteAsync(rig.Member, rig.Project,
            ProjectOperation.WriteTasks, (scope, _) =>
            {
                scope.Project.Name = "Member cannot edit project";
                scope.Record(ActivityAction.ProjectUpdated);
                return Task.FromResult(0);
            }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Transactions.ExecuteAsync(rig.Owner, rig.Project,
            ProjectOperation.ManageProject, (scope, _) =>
            {
                scope.Project.OwnerId = rig.Member;
                scope.Record(ActivityAction.ProjectUpdated);
                return Task.FromResult(0);
            }));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivityCannotReferenceAnotherProjectsTaskAndCancellationDoesNotCommit()
    {
        await using var rig = await Rig.CreateAsync(fixture);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Transactions.ExecuteAsync(rig.Owner, rig.Project,
            ProjectOperation.ManageProject, (scope, _) =>
            {
                scope.Project.Name = "Wrong task";
                scope.Record(ActivityAction.ProjectUpdated, taskId: rig.OtherTask);
                return Task.FromResult(0);
            }));
        await Assert.ThrowsAsync<OperationCanceledException>(() => rig.Transactions.ExecuteAsync<int>(rig.Owner,
            rig.Project, ProjectOperation.ManageProject, (scope, _) =>
            {
                scope.Project.Name = "Cancelled";
                throw new OperationCanceledException();
            }));
        await using var database = fixture.CreateContext();
        Assert.Equal("Original", (await database.Projects.SingleAsync(project => project.Id == rig.Project)).Name);
        Assert.False(await database.ActivityLogs.AnyAsync(activity => activity.ProjectId == rig.Project));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task WaitingWriterRechecksMembershipAfterParentLockIsReleased()
    {
        var probe = new LockProbe();
        await using var rig = await Rig.CreateAsync(fixture, probe);
        await using var database = fixture.CreateContext();
        await using var held = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await database.Projects.FromSqlInterpolated($"SELECT * FROM `Projects` WHERE `Id` = {rig.Project} FOR UPDATE")
            .IgnoreQueryFilters().ToListAsync();
        database.ProjectMembers.Remove(await database.ProjectMembers.SingleAsync(member => member.ProjectId == rig.Project && member.UserId == rig.Member));
        await database.SaveChangesAsync();
        var called = false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pending = rig.Transactions.ExecuteAsync(rig.Member, rig.Project, ProjectOperation.WriteTasks,
            (_, _) => { called = true; return Task.FromResult(0); }, deadline.Token);
        await probe.Attempt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        var blocked = !pending.IsCompleted;
        await held.CommitAsync();
        var denial = await Assert.ThrowsAsync<ProjectAccessException>(() => pending);
        Assert.True(blocked);
        Assert.Equal(ProjectAccessDecision.NotFound, denial.Decision);
        Assert.False(called);
    }

    private sealed class CorruptActivityActor : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public bool SawPreviouslySavedProject { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled)
                foreach (var activity in eventData.Context!.ChangeTracker.Entries<ActivityLog>().Where(entry => entry.State == EntityState.Added))
                {
                    SawPreviouslySavedProject = eventData.Context.ChangeTracker.Entries<Project>().All(entry => entry.State == EntityState.Unchanged);
                    activity.Entity.ActorUserId = long.MaxValue; // Test-only injection forces a real FK failure on the second save.
                }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class LockProbe : DbCommandInterceptor
    {
        public TaskCompletionSource Attempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal)) Attempt.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Rig(MySqlFixture fixture, ServiceProvider provider, AsyncServiceScope scope) : IAsyncDisposable
    {
        public Clock Clock { get; } = new();
        public long Owner { get; private set; }
        public long Member { get; private set; }
        public long Outsider { get; private set; }
        public long Project { get; private set; }
        public long Task { get; private set; }
        public long OtherTask { get; private set; }
        public IProjectAccess Access => scope.ServiceProvider.GetRequiredService<IProjectAccess>();
        public IProjectTransactions Transactions => scope.ServiceProvider.GetRequiredService<IProjectTransactions>();

        public static async Task<Rig> CreateAsync(MySqlFixture fixture, IInterceptor? interceptor = null)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:TaskFlow"] = fixture.ConnectionString }).Build();
            var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration);
            Rig? rig = null;
            services.AddSingleton<TimeProvider>(_ => rig!.Clock);
            if (interceptor is not null) services.AddDbContext<TaskFlowDbContext>(options => options.AddInterceptors(interceptor));
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            rig = new Rig(fixture, provider, provider.CreateAsyncScope());
            await using var database = fixture.CreateContext(rig.Clock);
            ApplicationUser User(string name)
            {
                var email = $"foundation-{Guid.NewGuid():N}@example.test";
                return new() { DisplayName = name, Email = email, NormalizedEmail = email.ToUpperInvariant() };
            }
            var owner = User("Owner"); var member = User("Member"); var outsider = User("Outsider");
            database.Users.AddRange(owner, member, outsider);
            await database.SaveChangesAsync();
            rig.Owner = owner.Id; rig.Member = member.Id; rig.Outsider = outsider.Id;
            var project = new Project { OwnerId = owner.Id, Name = "Original" };
            var other = new Project { OwnerId = outsider.Id, Name = "Other" };
            database.Projects.AddRange(project, other);
            await database.SaveChangesAsync();
            rig.Project = project.Id;
            database.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = member.Id });
            var task = new TaskItem { ProjectId = project.Id, Title = "Task" };
            var otherTask = new TaskItem { ProjectId = other.Id, Title = "Other task" };
            database.Tasks.AddRange(task, otherTask);
            await database.SaveChangesAsync();
            rig.Task = task.Id; rig.OtherTask = otherTask.Id;
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await scope.DisposeAsync();
            await provider.DisposeAsync();
            await using var database = fixture.CreateContext();
            long[] users = [Owner, Member, Outsider];
            var projects = await database.Projects.IgnoreQueryFilters().Where(project => users.Contains(project.OwnerId))
                .Select(project => project.Id).ToArrayAsync();
            await using var transaction = await database.Database.BeginTransactionAsync();
            await database.ActivityLogs.IgnoreQueryFilters().Where(activity => projects.Contains(activity.ProjectId)).ExecuteDeleteAsync();
            await database.Tasks.IgnoreQueryFilters().Where(task => projects.Contains(task.ProjectId)).ExecuteDeleteAsync();
            await database.ProjectMembers.IgnoreQueryFilters().Where(member => projects.Contains(member.ProjectId)).ExecuteDeleteAsync();
            await database.Projects.IgnoreQueryFilters().Where(project => projects.Contains(project.Id)).ExecuteDeleteAsync();
            await database.Users.Where(user => users.Contains(user.Id)).ExecuteDeleteAsync();
            await transaction.CommitAsync();
        }
    }
}
