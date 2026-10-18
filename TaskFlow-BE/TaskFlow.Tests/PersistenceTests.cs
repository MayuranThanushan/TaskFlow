using TaskFlow.Tests.Fixtures;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MySql.Data.MySqlClient;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Infrastructure;

namespace TaskFlow.Tests;

[Collection(MySqlCollection.Name)]
public class PersistenceTests(MySqlFixture fixture)
{
    private TaskFlowDbContext Context(TimeProvider? clock = null) => fixture.CreateContext(clock);

    private static async Task<(ApplicationUser User, Project Project, TaskItem Task)> Seed(TaskFlowDbContext context)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { DisplayName = "Test User", Email = email, NormalizedEmail = email.ToUpperInvariant() };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        var project = new Project { OwnerId = user.Id, Name = "Test Project" };
        context.Projects.Add(project);
        await context.SaveChangesAsync();
        var task = new TaskItem { ProjectId = project.Id, Title = "Test Task" };
        context.Tasks.Add(task);
        await context.SaveChangesAsync();
        return (user, project, task);
    }

    private static async Task<ApplicationUser> AddMember(TaskFlowDbContext context, Project project)
    {
        var email = $"{Guid.NewGuid():N}@example.test";
        var member = new ApplicationUser { DisplayName = "Member", Email = email, NormalizedEmail = email.ToUpperInvariant() };
        context.Users.Add(member);
        await context.SaveChangesAsync();
        context.ProjectMembers.Add(new ProjectMember { ProjectId = project.Id, UserId = member.Id });
        return member;
    }

    [MySqlFact]
    [Trait("Category", "MySql")]
    public async Task IdentityUsesTheSharedUserStoreAndHashesPasswords()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TaskFlow"] = fixture.ConnectionString
        }).Build();
        await using var services = new ServiceCollection().AddLogging().AddInfrastructure(configuration).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{Guid.NewGuid():N}@example.test";
        var user = new ApplicationUser { DisplayName = "Identity User", Email = email, UserName = email };
        const string password = "ExampleOnly-Pass123!";
        var result = await manager.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(',', result.Errors.Select(error => error.Code)));
        Assert.True(user.Id > 0);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.True(await manager.CheckPasswordAsync(user, password));
        Assert.Equal(user.Id, (await manager.FindByEmailAsync(email.ToUpperInvariant()))!.Id);
        Assert.True(user.CreatedAtUtc.Kind == DateTimeKind.Utc);
    }

    [MySqlFact]
    [Trait("Category", "MySql")]
    public async Task MigrationAppliesToRealMySqlAndIsUpToDate()
    {
        await using var context = Context();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Single(await context.Database.GetAppliedMigrationsAsync());
        var script = context.Database.GenerateCreateScript();
        Assert.Contains("CK_Tasks_Status", script);
        Assert.Contains("CK_Tasks_Priority", script);
        Assert.All(context.Model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()),
            foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    [MySqlFact]
    [Trait("Category", "MySql")]
    public async Task DeletedTasksAndProjectsAreHiddenWhileHistoryIsRetained()
    {
        await using var context = Context();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var (user, project, task) = await Seed(context);
        context.ActivityLogs.Add(new ActivityLog { ProjectId = project.Id, ActorUserId = user.Id, TaskId = task.Id, ActionType = ActivityAction.TaskCreated });
        await AddMember(context, project);
        await context.SaveChangesAsync();
        task.DeletedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();
        Assert.Empty(await context.Tasks.Where(value => value.ProjectId == project.Id).ToListAsync());
        Assert.Single(await context.ActivityLogs.Where(value => value.ProjectId == project.Id).ToListAsync());
        project.DeletedAtUtc = DateTime.UtcNow;
        await context.SaveChangesAsync();
        Assert.Empty(await context.Projects.Where(value => value.Id == project.Id).ToListAsync());
        Assert.Empty(await context.ProjectMembers.Where(value => value.ProjectId == project.Id).ToListAsync());
        Assert.Empty(await context.ActivityLogs.Where(value => value.ProjectId == project.Id).ToListAsync());
        Assert.Single(await context.ActivityLogs.IgnoreQueryFilters().Where(value => value.ProjectId == project.Id).ToListAsync());
        Assert.Single(await context.Tasks.IgnoreQueryFilters().Where(value => value.ProjectId == project.Id).ToListAsync());
    }

    [MySqlFact]
    [Trait("Category", "MySql")]
    public async Task DatabaseRejectsInvalidEnumsForeignKeysAndDuplicateMemberships()
    {
        await using var context = Context();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var (user, project, task) = await Seed(context);
        var statusError = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Tasks SET Status = {"Invalid"} WHERE Id = {task.Id}"));
        Assert.Equal(3819, statusError.Number);
        var priorityError = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Tasks SET Priority = {"Invalid"} WHERE Id = {task.Id}"));
        Assert.Equal(3819, priorityError.Number);
        var foreignKeyError = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE Tasks SET ProjectId = {long.MaxValue} WHERE Id = {task.Id}"));
        Assert.Equal(1452, foreignKeyError.Number);
        var member = await AddMember(context, project);
        await context.SaveChangesAsync();
        var duplicateError = await Assert.ThrowsAsync<MySqlException>(() => context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO ProjectMembers (ProjectId, UserId, Role, JoinedAtUtc) VALUES ({project.Id}, {member.Id}, {"Member"}, {DateTime.UtcNow})"));
        Assert.Equal(1062, duplicateError.Number);
    }

    [MySqlFact]
    [Trait("Category", "MySql")]
    public async Task NormalizedEmailsAreUniqueInTheActualDatabase()
    {
        await using var context = Context();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var (user, _, _) = await Seed(context);
        context.Users.Add(new ApplicationUser { DisplayName = "Duplicate", Email = user.Email!.ToUpperInvariant(), NormalizedEmail = user.NormalizedEmail! });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(1062, Assert.IsType<MySqlException>(error.InnerException).Number);
    }

    [MySqlFact]
    [Trait("Category", "MySql")]
    public async Task UtcTimestampsAndNullableDeadlinesRoundTrip()
    {
        var clock = new TestClock();
        await using var context = Context(clock);
        await using var transaction = await context.Database.BeginTransactionAsync();
        var (_, _, task) = await Seed(context);
        var created = task.CreatedAtUtc;
        clock.Now = clock.Now.AddHours(2);
        task.DueDateUtc = clock.Now.UtcDateTime;
        task.Title = "Changed";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var loaded = await context.Tasks.SingleAsync(value => value.Id == task.Id);
        Assert.Equal(created, loaded.CreatedAtUtc);
        Assert.Equal(clock.Now.UtcDateTime, loaded.UpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, loaded.DueDateUtc!.Value.Kind);
        Assert.Equal(clock.Now.UtcDateTime, loaded.DueDateUtc.Value);
        loaded.DueDateUtc = null;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        Assert.Null((await context.Tasks.SingleAsync(value => value.Id == task.Id)).DueDateUtc);
    }

    [MySqlFact]
    [Trait("Category", "MySql")]
    public async Task DataAndActivityRollBackTogetherAndHistoryCannotBeEdited()
    {
        await using var context = Context();
        long projectId;
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var (user, project, task) = await Seed(context);
            projectId = project.Id;
            var activity = new ActivityLog { ProjectId = project.Id, ActorUserId = user.Id, TaskId = task.Id, ActionType = ActivityAction.TaskCreated };
            context.ActivityLogs.Add(activity);
            await context.SaveChangesAsync();
            activity.Details = "Changed history";
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
            context.Entry(activity).State = EntityState.Unchanged;
            context.Remove(task);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
            await transaction.RollbackAsync();
        }
        context.ChangeTracker.Clear();
        Assert.False(await context.Projects.IgnoreQueryFilters().AnyAsync(value => value.Id == projectId));
        Assert.False(await context.ActivityLogs.IgnoreQueryFilters().AnyAsync(value => value.ProjectId == projectId));
    }

    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
