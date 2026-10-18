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
    private static void Totals(JsonElement body, int projects, int tasks, int toDo, int inProgress, int done, int assigned, int overdue)
    {
        Assert.Equal(projects, body.GetProperty("totalProjects").GetInt32()); Assert.Equal(tasks, body.GetProperty("totalTasks").GetInt32()); Assert.Equal(assigned, body.GetProperty("assignedToMe").GetInt32()); Assert.Equal(overdue, body.GetProperty("overdueTasks").GetInt32());
        var status = body.GetProperty("tasksByStatus"); Assert.Equal(toDo, status.GetProperty("toDo").GetInt32()); Assert.Equal(inProgress, status.GetProperty("inProgress").GetInt32()); Assert.Equal(done, status.GetProperty("done").GetInt32()); Assert.Equal(tasks, toDo + inProgress + done);
        Assert.Equal(5, body.EnumerateObject().Count()); Assert.Equal(3, status.EnumerateObject().Count());
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task DashboardRequiresAuthenticationHasNoStoreAndReturnsZeroForAnEmptyWorkspace()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var anonymous = host.Client(); using var owner = await host.LoginAsync(0);
        await Problem(await anonymous.GetAsync("/api/v1/dashboard"), HttpStatusCode.Unauthorized);
        var response = await owner.GetAsync("/api/v1/dashboard"); Assert.True(response.Headers.CacheControl?.NoStore); Assert.True(response.Headers.Contains("X-Trace-Id")); Totals(await Body(response), 0, 0, 0, 0, 0, 0, 0);
        await Problem(await owner.GetAsync("/api/v1/dashboard?userId=1"), HttpStatusCode.BadRequest);
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task DashboardCountsCurrentScopeOnceAndHonorsUtcEqualityNullCompletionAndDeletion()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1); using var outsider = await host.LoginAsync(2);
        var project = long.Parse((await Body(await host.CreateAsync(owner, "Owned"))).GetProperty("id").GetString()!);
        var shared = long.Parse((await Body(await host.CreateAsync(member, "Shared"))).GetProperty("id").GetString()!);
        var hidden = long.Parse((await Body(await host.CreateAsync(outsider, "Private"))).GetProperty("id").GetString()!);
        var deleted = long.Parse((await Body(await host.CreateAsync(owner, "Deleted"))).GetProperty("id").GetString()!);
        await Body(await host.CreateAsync(owner, "Empty"));
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/projects/" + project + "/members", new { userId = host.Users[1].Id.ToString() }));
        await Body(await host.MemberWriteAsync(member, HttpMethod.Post, "/api/v1/projects/" + shared + "/members", new { userId = host.Users[0].Id.ToString() }));
        var now = host.Clock.Now.UtcDateTime;
        await using (var db = fixture.CreateContext(host.Clock))
        {
            // Redundant owner/member data and multiple memberships must not multiply counts.
            db.ProjectMembers.AddRange(new ProjectMember { ProjectId = project, UserId = host.Users[0].Id }, new ProjectMember { ProjectId = project, UserId = host.Users[2].Id });
            TaskItem Row(TaskState status, DateTime? due, long? assignee) => new() { ProjectId = project, Title = "Scoped task", Status = status, DueDateUtc = due, AssigneeId = assignee };
            db.Tasks.AddRange(Row(TaskState.ToDo, now.AddTicks(-10), host.Users[0].Id), Row(TaskState.InProgress, now.AddSeconds(-1), host.Users[1].Id), Row(TaskState.Done, now.AddDays(-1), host.Users[0].Id), Row(TaskState.ToDo, now, host.Users[0].Id), Row(TaskState.InProgress, now.AddTicks(10), null), Row(TaskState.ToDo, null, null), Row(TaskState.Done, null, host.Users[1].Id));
            var removedTask = Row(TaskState.ToDo, now.AddDays(-1), host.Users[0].Id); removedTask.DeletedAtUtc = now; db.Tasks.Add(removedTask);
            db.Tasks.AddRange(new TaskItem { ProjectId = shared, Title = "Shared task", DueDateUtc = now.AddDays(-1), AssigneeId = host.Users[0].Id }, new TaskItem { ProjectId = hidden, Title = "Hidden task", DueDateUtc = now.AddDays(-1), AssigneeId = host.Users[0].Id }, new TaskItem { ProjectId = deleted, Title = "Retained task", DueDateUtc = now.AddDays(-1), AssigneeId = host.Users[0].Id }); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.NoContent, (await host.WriteAsync(owner, HttpMethod.Delete, deleted.ToString())).StatusCode);
        Totals(await Body(await owner.GetAsync("/api/v1/dashboard")), 3, 8, 4, 2, 2, 4, 3);
        Totals(await Body(await member.GetAsync("/api/v1/dashboard")), 2, 8, 4, 2, 2, 2, 3);
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task DashboardAndProgressRefreshAfterCreationAssignmentStatusRemovalAndSoftDeletion()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1);
        var project = (await Body(await host.CreateAsync(owner, "Live totals"))).GetProperty("id").GetString()!; var root = "/api/v1/projects/" + project;
        Totals(await Body(await owner.GetAsync("/api/v1/dashboard")), 1, 0, 0, 0, 0, 0, 0);
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/members", new { userId = host.Users[1].Id.ToString() }));
        var task = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/tasks", new { title = "Due", assigneeId = host.Users[0].Id.ToString(), dueDateUtc = "2026-10-17T12:00:00Z" }))).GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + task;
        Totals(await Body(await owner.GetAsync("/api/v1/dashboard")), 1, 1, 1, 0, 0, 1, 1);
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { assigneeId = host.Users[1].Id.ToString() })); Totals(await Body(await member.GetAsync("/api/v1/dashboard")), 1, 1, 1, 0, 0, 1, 1);
        await Body(await host.MemberWriteAsync(member, HttpMethod.Patch, path + "/status", new { status = "Done" })); Totals(await Body(await owner.GetAsync("/api/v1/dashboard")), 1, 1, 0, 0, 1, 0, 0); Assert.Equal(100, (await Body(await owner.GetAsync(root))).GetProperty("progressPercentage").GetInt32());
        await Body(await host.MemberWriteAsync(member, HttpMethod.Patch, path + "/status", new { status = "ToDo" })); Assert.Equal(0, (await Body(await owner.GetAsync(root))).GetProperty("progressPercentage").GetInt32());
        var completed = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/tasks", new { title = "Complete" }))).GetProperty("id").GetString()!;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, "/api/v1/tasks/" + completed + "/status", new { status = "Done" })); Assert.Equal(50, (await Body(await owner.GetAsync(root))).GetProperty("progressPercentage").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, root + "/members/" + host.Users[1].Id)).StatusCode); Totals(await Body(await member.GetAsync("/api/v1/dashboard")), 0, 0, 0, 0, 0, 0, 0);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, "/api/v1/tasks/" + completed)).StatusCode); Totals(await Body(await owner.GetAsync("/api/v1/dashboard")), 1, 1, 1, 0, 0, 0, 1);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, path)).StatusCode); Assert.Equal(0, (await Body(await owner.GetAsync(root))).GetProperty("progressPercentage").GetInt32());
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/tasks", new { title = "Hidden with parent" })); Assert.Equal(HttpStatusCode.NoContent, (await host.WriteAsync(owner, HttpMethod.Delete, project)).StatusCode); Totals(await Body(await owner.GetAsync("/api/v1/dashboard")), 0, 0, 0, 0, 0, 0, 0);
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task ProjectProgressUsesActiveTasksWithZeroFullAndMidpointRounding()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        foreach (var row in new (int Total, int Done, int Percent)[] { (0, 0, 0), (8, 1, 13), (3, 1, 33), (2, 1, 50), (8, 8, 100), (9, 8, 89) })
        {
            var project = long.Parse((await Body(await host.CreateAsync(owner, "Progress"))).GetProperty("id").GetString()!);
            await using (var db = fixture.CreateContext()) { for (var i = 0; i < row.Total; i++) db.Tasks.Add(new TaskItem { ProjectId = project, Title = "Active", Status = i < row.Done ? TaskState.Done : TaskState.InProgress }); db.Tasks.Add(new TaskItem { ProjectId = project, Title = "Deleted completed", Status = TaskState.Done, DeletedAtUtc = DateTime.UtcNow }); await db.SaveChangesAsync(); }
            var response = await Body(await owner.GetAsync("/api/v1/projects/" + project)); Assert.Equal(row.Total, response.GetProperty("totalTasks").GetInt32()); Assert.Equal(row.Done, response.GetProperty("completedTasks").GetInt32()); Assert.Equal(row.Percent, response.GetProperty("progressPercentage").GetInt32());
        }
    }
    private sealed class DashboardCountGate : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Counted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal) && command.CommandText.Contains("`Projects`", StringComparison.Ordinal) && !command.CommandText.Contains("`Tasks`", StringComparison.Ordinal)) { Counted.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task DashboardUsesOneSnapshotAndCapturedUtcInstantAcrossConcurrentCreationAndClockAdvance()
    {
        var gate = new DashboardCountGate(); await using var host = new ProjectHost(fixture, gate); await host.InitializeAsync(); using var reader = await host.LoginAsync(0); using var writer = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(writer, "Snapshot"))).GetProperty("id").GetString()!;
        await Body(await host.MemberWriteAsync(writer, HttpMethod.Post, "/api/v1/projects/" + project + "/tasks", new { title = "Boundary", dueDateUtc = host.Clock.Now.AddSeconds(30).ToString("O") }));
        gate.Armed = true; var pending = reader.GetAsync("/api/v1/dashboard");
        try { await gate.Counted.Task.WaitAsync(TimeSpan.FromSeconds(10)); host.Clock.Now = host.Clock.Now.AddMinutes(1); var created = (await Body(await host.CreateAsync(writer, "Concurrent"))).GetProperty("id").GetString()!; await Body(await host.MemberWriteAsync(writer, HttpMethod.Post, "/api/v1/projects/" + created + "/tasks", new { title = "Concurrent", assigneeId = host.Users[0].Id.ToString() })); }
        finally { gate.Armed = false; gate.Release.TrySetResult(); }
        Totals(await Body(await pending.WaitAsync(TimeSpan.FromSeconds(10))), 1, 1, 1, 0, 0, 0, 0);
        Totals(await Body(await reader.GetAsync("/api/v1/dashboard")), 2, 2, 2, 0, 0, 1, 1);
    }
}
