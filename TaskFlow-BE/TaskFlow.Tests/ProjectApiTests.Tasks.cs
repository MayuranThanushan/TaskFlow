using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact, Trait("Category", "MySql")]
    public async Task TasksCreateDefaultsAndMemberAssignmentPersistWithGeneratedActivityIds()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1);
        var id = (await Body(await host.CreateAsync(owner, "Tasks"))).GetProperty("id").GetString()!; var path = "/api/v1/projects/" + id;
        var response = await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/tasks", new { title = " First ", description = " " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var task = await Body(response);
        Assert.Equal("First", task.GetProperty("title").GetString()); Assert.Equal("ToDo", task.GetProperty("status").GetString()); Assert.Equal("Medium", task.GetProperty("priority").GetString());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("description").ValueKind); Assert.Equal(JsonValueKind.Null, task.GetProperty("assigneeId").ValueKind); Assert.Equal(JsonValueKind.Null, task.GetProperty("dueDateUtc").ValueKind);
        Assert.Equal(10, task.EnumerateObject().Count()); Assert.Equal(id, task.GetProperty("projectId").GetString());
        Assert.Equal("/api/v1/tasks/" + task.GetProperty("id").GetString(), response.Headers.Location!.AbsolutePath);
        Assert.Equal(task.ToString(), (await Body(await owner.GetAsync(response.Headers.Location))).ToString());
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/members", new { userId = host.Users[1].Id.ToString() }));
        var assigned = await Body(await host.MemberWriteAsync(member, HttpMethod.Post, path + "/tasks", new { title = "Assigned", priority = "High", assigneeId = host.Users[0].Id.ToString(), dueDateUtc = "2026-11-01T15:30:00.1234567+05:30" }));
        Assert.Equal("2026-11-01T10:00:00.123456Z", assigned.GetProperty("dueDateUtc").GetString()); Assert.Equal(host.Users[0].Id.ToString(), assigned.GetProperty("assigneeId").GetString());
        var progress = await Body(await owner.GetAsync(path)); Assert.Equal(2, progress.GetProperty("totalTasks").GetInt32()); Assert.Equal(0, progress.GetProperty("progressPercentage").GetInt32());
        await using var db = fixture.CreateContext(); var taskId = long.Parse(assigned.GetProperty("id").GetString()!);
        var events = await db.ActivityLogs.Where(a => a.TaskId == taskId).ToListAsync(); Assert.Equal(2, events.Count);
        Assert.Contains(events, a => a.ActionType == ActivityAction.TaskCreated); Assert.Contains(events, a => a.ActionType == ActivityAction.TaskAssigned); Assert.All(events, a => Assert.Equal(host.Users[1].Id, a.ActorUserId));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskCreationValidatesBodyDeadlinesIdsEligibilityAndCsrf()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var anonymous = host.Client(); using var outsider = await host.LoginAsync(2);
        var id = (await Body(await host.CreateAsync(owner, "Validation"))).GetProperty("id").GetString()!; var path = "/api/v1/projects/" + id + "/tasks";
        await Problem(await host.MemberWriteAsync(anonymous, HttpMethod.Post, path, new { title = "Task" }), HttpStatusCode.Unauthorized);
        using var noCsrf = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(new { title = "Task" }) }; await Problem(await owner.SendAsync(noCsrf), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(outsider, HttpMethod.Post, path, new { title = "Task" }), HttpStatusCode.NotFound);
        foreach (var body in new object[] { new { title = " " }, new { title = new string('x', 201) }, new { title = "T", description = new string('x', 10001) }, new { title = "T", priority = "Urgent" }, new { title = "T", priority = (string?)null }, new { title = "T", priority = 1 }, new { title = "T", status = "Done" }, new { title = "T", projectId = id }, new { title = "T", assigneeId = "0" }, new { title = "T", assigneeId = "9223372036854775808" }, new { title = "T", assigneeId = 1 }, new { title = "T", dueDateUtc = "2026-11-01T12:00:00" }, new { title = "T", dueDateUtc = "2026-02-30T12:00:00Z" }, new { title = "T", dueDateUtc = "0001-01-01T00:00:00Z" } })
            await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, path, body), HttpStatusCode.BadRequest);
        foreach (var assignee in new[] { host.Users[2].Id.ToString(), long.MaxValue.ToString() })
        {
            var rejected = await host.MemberWriteAsync(owner, HttpMethod.Post, path, new { title = "T", assigneeId = assignee }); var error = await Problem(rejected, HttpStatusCode.BadRequest);
            Assert.True(error.GetProperty("errors").TryGetProperty("assigneeId", out _));
        }
        await using var db = fixture.CreateContext(); Assert.False(await db.Tasks.AnyAsync(t => t.ProjectId == long.Parse(id)));
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path, new { title = new string('x', 200), description = new string('x', 10000), assigneeId = host.Users[0].Id.ToString(), dueDateUtc = (string?)null }));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskReadsHideDeletedParentsTasksOutsidersAndRemovedMembersAndPreserveBigint()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1); using var outsider = await host.LoginAsync(2);
        var id = (await Body(await host.CreateAsync(owner, "Privacy"))).GetProperty("id").GetString()!; var path = "/api/v1/projects/" + id;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/members", new { userId = host.Users[1].Id.ToString() }));
        const long taskId = 9_007_199_254_740_999;
        await using (var db = fixture.CreateContext()) { db.Tasks.Add(new TaskItem { Id = taskId, ProjectId = long.Parse(id), Title = "Large task" }); await db.SaveChangesAsync(); }
        Assert.Equal(taskId.ToString(), (await Body(await member.GetAsync("/api/v1/tasks/" + taskId))).GetProperty("id").GetString());
        await Problem(await outsider.GetAsync("/api/v1/tasks/" + taskId), HttpStatusCode.NotFound); await Problem(await outsider.GetAsync(path + "/tasks"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, path + "/members/" + host.Users[1].Id)).StatusCode);
        await Problem(await member.GetAsync("/api/v1/tasks/" + taskId), HttpStatusCode.NotFound); await Problem(await member.GetAsync(path + "/tasks"), HttpStatusCode.NotFound);
        await using (var db = fixture.CreateContext()) { var t = await db.Tasks.SingleAsync(t => t.Id == taskId); t.DeletedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync(); }
        await Problem(await owner.GetAsync("/api/v1/tasks/" + taskId), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "/tasks"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await host.WriteAsync(owner, HttpMethod.Delete, id)).StatusCode);
        await Problem(await owner.GetAsync(path + "/tasks"), HttpStatusCode.NotFound); await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/tasks", new { title = "Gone" }), HttpStatusCode.NotFound);
        foreach (var invalid in new[] { "0", "-1", "1.5", "9223372036854775808", "abc" }) await Problem(await owner.GetAsync("/api/v1/tasks/" + invalid), HttpStatusCode.BadRequest);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskListsCombineLiteralFiltersUseStablePaginationAndSortNullDeadlinesLast()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var id = (await Body(await host.CreateAsync(owner, "Queries"))).GetProperty("id").GetString()!; var path = "/api/v1/projects/" + id + "/tasks"; var projectId = long.Parse(id);
        await using (var db = fixture.CreateContext())
        {
            for (var index = 0; index < 23; index++) db.Tasks.Add(new TaskItem { ProjectId = projectId, Title = index == 0 ? "100%_Ready" : "Task " + index.ToString("D2"), Priority = index % 2 == 0 ? TaskPriority.High : TaskPriority.Low, Status = index % 2 == 0 ? TaskState.Done : TaskState.ToDo, AssigneeId = host.Users[0].Id, DueDateUtc = index < 2 ? new DateTime(2026, 11, index + 1, 0, 0, 0, DateTimeKind.Utc) : null });
            await db.SaveChangesAsync();
        }
        var first = await Body(await owner.GetAsync(path + "?pageSize=10")); var second = await Body(await owner.GetAsync(path + "?pageSize=10&page=2"));
        Assert.Equal(23, first.GetProperty("totalCount").GetInt32()); Assert.Equal(3, first.GetProperty("totalPages").GetInt32()); Assert.Equal(10, second.GetProperty("items").GetArrayLength());
        Assert.Empty(first.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()).Intersect(second.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString())));
        Assert.Equal(1, (await Body(await owner.GetAsync(path + "?search=%25_&priority=High&status=Done&assigneeId=" + host.Users[0].Id))).GetProperty("totalCount").GetInt32());
        foreach (var direction in new[] { "asc", "desc" })
        {
            var items = (await Body(await owner.GetAsync(path + "?sortBy=dueDateUtc&sortDirection=" + direction + "&pageSize=100"))).GetProperty("items");
            Assert.NotEqual(JsonValueKind.Null, items[0].GetProperty("dueDateUtc").ValueKind); Assert.Equal(JsonValueKind.Null, items[22].GetProperty("dueDateUtc").ValueKind);
        }
        Assert.Equal("Low", (await Body(await owner.GetAsync(path + "?sortBy=priority&sortDirection=asc"))).GetProperty("items")[0].GetProperty("priority").GetString());
        Assert.Equal("High", (await Body(await owner.GetAsync(path + "?sortBy=priority&sortDirection=desc"))).GetProperty("items")[0].GetProperty("priority").GetString());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "?page=100"))).GetProperty("items").GetArrayLength());
        foreach (var query in new[] { "page=0", "pageSize=101", "page=2147483647&pageSize=100", "status=", "priority=", "assigneeId=", "status=9", "status=done", "priority=1", "assigneeId=0", "sortBy=ownerId", "sortDirection=sideways", "page=1&page=2", "unknown=1" }) await Problem(await owner.GetAsync(path + "?" + query), HttpStatusCode.BadRequest);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskCreationActivityFailureRollsBackTaskAndAssignment()
    {
        var failure = new FailActivity(); await using var host = new ProjectHost(fixture, failure); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var id = (await Body(await host.CreateAsync(owner, "Rollback"))).GetProperty("id").GetString()!; failure.Enabled = true;
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/projects/" + id + "/tasks", new { title = "Must roll back", assigneeId = host.Users[0].Id.ToString() }), HttpStatusCode.InternalServerError);
        await using var db = fixture.CreateContext(); Assert.False(await db.Tasks.AnyAsync(t => t.ProjectId == long.Parse(id))); Assert.Equal(1, await db.ActivityLogs.CountAsync(a => a.ProjectId == long.Parse(id)));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ConcurrentMemberRemovalAndAssignedTaskCreationCannotLeaveAnIneligibleAssignee()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var writer = await host.LoginAsync(0);
        var id = (await Body(await host.CreateAsync(owner, "Race"))).GetProperty("id").GetString()!; var path = "/api/v1/projects/" + id;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/members", new { userId = host.Users[1].Id.ToString() }));
        var results = await Task.WhenAll(host.MemberWriteAsync(writer, HttpMethod.Post, path + "/tasks", new { title = "Racing assignment", assigneeId = host.Users[1].Id.ToString() }), host.MemberWriteAsync(owner, HttpMethod.Delete, path + "/members/" + host.Users[1].Id));
        Assert.Contains(results[0].StatusCode, new[] { HttpStatusCode.Created, HttpStatusCode.BadRequest }); Assert.Equal(HttpStatusCode.NoContent, results[1].StatusCode);
        await using var db = fixture.CreateContext(); Assert.False(await db.Tasks.AnyAsync(t => t.ProjectId == long.Parse(id) && t.AssigneeId != null));
    }
}
