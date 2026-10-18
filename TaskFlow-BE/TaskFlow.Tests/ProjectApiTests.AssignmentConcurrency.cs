using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    // The first request really owns MySQL's parent row lock before the second attempts it.
    // Bounded gates and finally-release prevent a failed assertion from stranding a transaction.
    private sealed class ParentLockGate : DbCommandInterceptor
    {
        private DbCommand? first;
        private int attempts;
        public bool Armed { get; set; }
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref attempts) == 1) first = command;
                else Queued.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (ReferenceEquals(first, command))
            {
                Held.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            }
            return result;
        }
        public async Task<(HttpResponseMessage First, HttpResponseMessage Second)> OrderedAsync(Func<Task<HttpResponseMessage>> firstRequest, Func<Task<HttpResponseMessage>> secondRequest)
        {
            Armed = true;
            var one = firstRequest(); Task<HttpResponseMessage>? two = null;
            try
            {
                await Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
                two = secondRequest(); await Queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(150); Assert.False(two.IsCompleted, "The competing HTTP write must wait for the parent lock.");
            }
            finally { Release.TrySetResult(); }
            return (await one.WaitAsync(TimeSpan.FromSeconds(15)), await two!.WaitAsync(TimeSpan.FromSeconds(15)));
        }
    }
    private sealed class FailRemovalActivity : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entry in data.Context!.ChangeTracker.Entries<ActivityLog>().Where(e => e.State == EntityState.Added && e.Entity.ActionType == ActivityAction.MemberRemoved)) entry.Entity.ActorUserId = long.MaxValue;
            return ValueTask.FromResult(result);
        }
    }
    private async Task<(string Root, string TaskPath, long TaskId)> AssignmentScenario(ProjectHost host, HttpClient owner, bool assigned = false)
    {
        var id = (await Body(await host.CreateAsync(owner, "Assignment boundary"))).GetProperty("id").GetString()!;
        var root = "/api/v1/projects/" + id;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/members", new { userId = host.Users[1].Id.ToString() }));
        var task = await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/tasks", new { title = "Original", assigneeId = assigned ? host.Users[1].Id.ToString() : null }));
        var taskId = long.Parse(task.GetProperty("id").GetString()!);
        return (root, "/api/v1/tasks/" + taskId, taskId);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task AssignmentHoldingParentLockCommitsBeforeQueuedRemovalAndIsThenCleared()
    {
        var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, gate); await host.InitializeAsync();
        using var owner = await host.LoginAsync(0); using var remover = await host.LoginAsync(0); using var member = await host.LoginAsync(1);
        var scenario = await AssignmentScenario(host, owner);
        var responses = await gate.OrderedAsync(
            () => host.MemberWriteAsync(owner, HttpMethod.Patch, scenario.TaskPath, new { assigneeId = host.Users[1].Id.ToString() }),
            () => host.MemberWriteAsync(remover, HttpMethod.Delete, scenario.Root + "/members/" + host.Users[1].Id));
        Assert.Equal(HttpStatusCode.OK, responses.First.StatusCode); Assert.Equal(HttpStatusCode.NoContent, responses.Second.StatusCode);
        await using var db = fixture.CreateContext(); var task = await db.Tasks.SingleAsync(t => t.Id == scenario.TaskId); Assert.Null(task.AssigneeId);
        var events = await db.ActivityLogs.Where(a => a.ProjectId == task.ProjectId).OrderBy(a => a.Id).ToListAsync();
        var assigned = Assert.Single(events, a => a.ActionType == ActivityAction.TaskAssigned); var cleared = Assert.Single(events, a => a.ActionType == ActivityAction.TaskUnassigned); var removed = Assert.Single(events, a => a.ActionType == ActivityAction.MemberRemoved);
        Assert.True(assigned.Id < cleared.Id && cleared.Id < removed.Id); Assert.Equal(task.Id, cleared.TaskId);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/auth/me")).StatusCode); await Problem(await member.GetAsync(scenario.TaskPath), HttpStatusCode.NotFound);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task RemovalHoldingParentLockRejectsQueuedOwnerAssignmentWithoutPartialEditing()
    {
        var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, gate); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var writer = await host.LoginAsync(0);
        var scenario = await AssignmentScenario(host, owner);
        var responses = await gate.OrderedAsync(
            () => host.MemberWriteAsync(owner, HttpMethod.Delete, scenario.Root + "/members/" + host.Users[1].Id),
            () => host.MemberWriteAsync(writer, HttpMethod.Patch, scenario.TaskPath, new { title = "Must not persist", assigneeId = host.Users[1].Id.ToString() }));
        Assert.Equal(HttpStatusCode.NoContent, responses.First.StatusCode); var problem = await Problem(responses.Second, HttpStatusCode.BadRequest); Assert.True(problem.GetProperty("errors").TryGetProperty("assigneeId", out _));
        await using var db = fixture.CreateContext(); var task = await db.Tasks.SingleAsync(t => t.Id == scenario.TaskId); Assert.Equal("Original", task.Title); Assert.Null(task.AssigneeId);
        Assert.False(await db.ActivityLogs.AnyAsync(a => a.TaskId == task.Id && (a.ActionType == ActivityAction.TaskAssigned || a.ActionType == ActivityAction.TaskUpdated)));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public Task RemovedMemberQueuedCreationIsDeniedAfterRemovalCommits() => MemberWriteAfterRemoval("create");
    [MySqlFact, Trait("Category", "MySql")]
    public Task RemovedMemberQueuedEditingIsDeniedAfterRemovalCommits() => MemberWriteAfterRemoval("edit");
    [MySqlFact, Trait("Category", "MySql")]
    public Task RemovedMemberQueuedStatusChangeIsDeniedAfterRemovalCommits() => MemberWriteAfterRemoval("status");
    private async Task MemberWriteAfterRemoval(string operation)
    {
        var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, gate); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1);
        var scenario = await AssignmentScenario(host, owner, assigned: true);
        Task<HttpResponseMessage> Write() => operation switch
        {
            "create" => host.MemberWriteAsync(member, HttpMethod.Post, scenario.Root + "/tasks", new { title = "Denied create", assigneeId = host.Users[1].Id.ToString() }),
            "edit" => host.MemberWriteAsync(member, HttpMethod.Patch, scenario.TaskPath, new { title = "Denied edit" }),
            _ => host.MemberWriteAsync(member, HttpMethod.Patch, scenario.TaskPath + "/status", new { status = "Done" })
        };
        var responses = await gate.OrderedAsync(() => host.MemberWriteAsync(owner, HttpMethod.Delete, scenario.Root + "/members/" + host.Users[1].Id), Write);
        Assert.Equal(HttpStatusCode.NoContent, responses.First.StatusCode); await Problem(responses.Second, HttpStatusCode.NotFound);
        await using var db = fixture.CreateContext(); var task = await db.Tasks.SingleAsync(t => t.Id == scenario.TaskId); Assert.Equal("Original", task.Title); Assert.Equal(TaskState.ToDo, task.Status); Assert.Null(task.AssigneeId);
        Assert.Equal(1, await db.Tasks.CountAsync(t => t.ProjectId == task.ProjectId)); Assert.False(await db.ActivityLogs.AnyAsync(a => a.ProjectId == task.ProjectId && a.ActorUserId == host.Users[1].Id));
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task AlreadyAuthorizedMemberWritesFinishBeforeQueuedRemovalThenAllActiveAssignmentsAreCleared()
    {
        foreach (var operation in new[] { "create", "edit", "status" })
        {
            var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, gate); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1);
            var scenario = await AssignmentScenario(host, owner, assigned: true);
            Task<HttpResponseMessage> Write() => operation switch
            {
                "create" => host.MemberWriteAsync(member, HttpMethod.Post, scenario.Root + "/tasks", new { title = "Member-created", assigneeId = host.Users[1].Id.ToString() }),
                "edit" => host.MemberWriteAsync(member, HttpMethod.Patch, scenario.TaskPath, new { title = "Member-edited" }),
                _ => host.MemberWriteAsync(member, HttpMethod.Patch, scenario.TaskPath + "/status", new { status = "Done" })
            };
            var responses = await gate.OrderedAsync(Write, () => host.MemberWriteAsync(owner, HttpMethod.Delete, scenario.Root + "/members/" + host.Users[1].Id));
            Assert.Equal(operation == "create" ? HttpStatusCode.Created : HttpStatusCode.OK, responses.First.StatusCode); Assert.Equal(HttpStatusCode.NoContent, responses.Second.StatusCode);
            await using var db = fixture.CreateContext(); var task = await db.Tasks.SingleAsync(t => t.Id == scenario.TaskId); var tasks = await db.Tasks.Where(t => t.ProjectId == task.ProjectId).ToListAsync();
            Assert.All(tasks, t => Assert.Null(t.AssigneeId)); Assert.Equal(operation == "create" ? 2 : 1, tasks.Count);
            Assert.Equal(operation == "edit" ? "Member-edited" : "Original", task.Title); Assert.Equal(operation == "status" ? TaskState.Done : TaskState.ToDo, task.Status);
            Assert.Equal(tasks.Count, await db.ActivityLogs.CountAsync(a => a.ProjectId == task.ProjectId && a.ActionType == ActivityAction.TaskUnassigned));
            await Problem(await host.MemberWriteAsync(member, HttpMethod.Patch, scenario.TaskPath, new { title = "Too late" }), HttpStatusCode.NotFound);
        }
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task RemovalCleansEveryActiveStatusButPreservesDeletedOtherAssigneesAndOtherProjectsAndReadditionDoesNotRestoreAssignments()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1);
        var scenario = await AssignmentScenario(host, owner, assigned: true); var projectId = long.Parse(scenario.Root.Split('/').Last());
        var otherProject = long.Parse((await Body(await host.CreateAsync(owner, "Other project"))).GetProperty("id").GetString()!);
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/projects/" + otherProject + "/members", new { userId = host.Users[1].Id.ToString() }));
        await using (var db = fixture.CreateContext(host.Clock))
        {
            db.Tasks.AddRange(new TaskItem { ProjectId = projectId, Title = "In progress", Status = TaskState.InProgress, AssigneeId = host.Users[1].Id }, new TaskItem { ProjectId = projectId, Title = "Completed", Status = TaskState.Done, AssigneeId = host.Users[1].Id },
                new TaskItem { ProjectId = projectId, Title = "Deleted historical", DeletedAtUtc = host.Clock.Now.UtcDateTime, AssigneeId = host.Users[1].Id }, new TaskItem { ProjectId = projectId, Title = "Owner assigned", AssigneeId = host.Users[0].Id }, new TaskItem { ProjectId = otherProject, Title = "Other scope", AssigneeId = host.Users[1].Id });
            await db.SaveChangesAsync();
        }
        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, scenario.Root + "/members/" + host.Users[1].Id)).StatusCode);
        await using (var db = fixture.CreateContext())
        {
            var all = await db.Tasks.IgnoreQueryFilters().Where(t => t.ProjectId == projectId || t.ProjectId == otherProject).ToListAsync();
            var cleared = all.Where(t => t.ProjectId == projectId && t.DeletedAtUtc == null && t.Title != "Owner assigned").ToList(); Assert.Equal(3, cleared.Count);
            Assert.All(cleared, t => { Assert.Null(t.AssigneeId); Assert.Equal(host.Clock.Now.UtcDateTime, t.UpdatedAtUtc); Assert.True(t.CreatedAtUtc < t.UpdatedAtUtc); });
            Assert.Equal(host.Users[1].Id, all.Single(t => t.Title == "Deleted historical").AssigneeId); Assert.Equal(host.Users[1].Id, all.Single(t => t.Title == "Other scope").AssigneeId); Assert.Equal(host.Users[0].Id, all.Single(t => t.Title == "Owner assigned").AssigneeId);
            Assert.All(all.Except(cleared), t => Assert.True(t.UpdatedAtUtc < host.Clock.Now.UtcDateTime));
            var events = await db.ActivityLogs.Where(a => a.ProjectId == projectId && a.ActionType == ActivityAction.TaskUnassigned).ToListAsync(); Assert.Equal(3, events.Count); Assert.All(events, a => { Assert.Equal(host.Users[0].Id, a.ActorUserId); Assert.Equal(host.Users[1].Id.ToString(), JsonDocument.Parse(a.Details!).RootElement.GetProperty("userId").GetString()); Assert.Contains(cleared, t => t.Id == a.TaskId); });
        }
        await Problem(await member.GetAsync(scenario.Root), HttpStatusCode.NotFound); Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/projects/" + otherProject)).StatusCode);
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, scenario.Root + "/members", new { userId = host.Users[1].Id.ToString() }));
        await using (var db = fixture.CreateContext()) Assert.False(await db.Tasks.AnyAsync(t => t.ProjectId == projectId && t.AssigneeId == host.Users[1].Id));
        await Body(await host.MemberWriteAsync(member, HttpMethod.Patch, scenario.TaskPath, new { assigneeId = host.Users[1].Id.ToString() }));
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(scenario.TaskPath)).StatusCode);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task FailedRemovalRollsBackAllCleanupAndReleasesLockForAQueuedEligibleAssignment()
    {
        var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, gate, new FailRemovalActivity()); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var writer = await host.LoginAsync(0);
        var scenario = await AssignmentScenario(host, owner); var projectId = long.Parse(scenario.Root.Split('/').Last());
        await using (var db = fixture.CreateContext()) { for (var i = 0; i < 3; i++) db.Tasks.Add(new TaskItem { ProjectId = projectId, Title = "Rollback " + i, AssigneeId = host.Users[1].Id }); await db.SaveChangesAsync(); }
        var responses = await gate.OrderedAsync(() => host.MemberWriteAsync(owner, HttpMethod.Delete, scenario.Root + "/members/" + host.Users[1].Id), () => host.MemberWriteAsync(writer, HttpMethod.Patch, scenario.TaskPath, new { assigneeId = host.Users[1].Id.ToString() }));
        await Problem(responses.First, HttpStatusCode.InternalServerError); Assert.Equal(HttpStatusCode.OK, responses.Second.StatusCode);
        await using var database = fixture.CreateContext(); Assert.True(await database.ProjectMembers.AnyAsync(m => m.ProjectId == projectId && m.UserId == host.Users[1].Id));
        var tasks = await database.Tasks.Where(t => t.ProjectId == projectId).ToListAsync(); Assert.Equal(4, tasks.Count); Assert.All(tasks, t => Assert.Equal(host.Users[1].Id, t.AssigneeId));
        Assert.False(await database.ActivityLogs.AnyAsync(a => a.ProjectId == projectId && (a.ActionType == ActivityAction.MemberRemoved || a.ActionType == ActivityAction.TaskUnassigned)));
        Assert.Single(await database.ActivityLogs.Where(a => a.TaskId == scenario.TaskId && a.ActionType == ActivityAction.TaskAssigned).ToListAsync());
    }
}
