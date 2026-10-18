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
    public async Task TaskPatchPreservesOmittedFieldsClearsExplicitNullsAndNoOpsKeepAuditUnchanged()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(owner, "Patch"))).GetProperty("id").GetString()!;
        var created = await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/projects/" + project + "/tasks", new { title = "Original", description = "Context", priority = "High", assigneeId = host.Users[0].Id.ToString(), dueDateUtc = "2026-11-01T12:00:00.123456Z" }));
        var id = created.GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + id;
        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        var noop = await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { title = " Original " }));
        Assert.Equal(created.GetProperty("updatedAtUtc").GetString(), noop.GetProperty("updatedAtUtc").GetString());
        var renamed = await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { title = " Renamed " }));
        foreach (var field in new[] { "description", "priority", "assigneeId", "dueDateUtc", "createdAtUtc", "projectId", "status" }) Assert.Equal(created.GetProperty(field).ToString(), renamed.GetProperty(field).ToString());
        Assert.NotEqual(created.GetProperty("updatedAtUtc").GetString(), renamed.GetProperty("updatedAtUtc").GetString());
        var cleared = await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { description = (string?)null, assigneeId = (string?)null, dueDateUtc = (string?)null }));
        foreach (var field in new[] { "description", "assigneeId", "dueDateUtc" }) Assert.Equal(JsonValueKind.Null, cleared.GetProperty(field).ValueKind);
        foreach (var status in new[] { "Done", "ToDo", "InProgress" })
        {
            var response = await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path + "/status", new { status })); Assert.Equal(status, response.GetProperty("status").GetString()); Assert.Equal(3, response.EnumerateObject().Count());
        }
        host.Clock.Now = host.Clock.Now.AddMinutes(1);
        var before = await Body(await owner.GetAsync(path));
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path + "/status", new { status = "InProgress" }));
        Assert.Equal(before.GetProperty("updatedAtUtc").GetString(), (await Body(await owner.GetAsync(path))).GetProperty("updatedAtUtc").GetString());
        await using var db = fixture.CreateContext(); Assert.Equal(3, await db.ActivityLogs.CountAsync(a => a.TaskId == long.Parse(id) && a.ActionType == ActivityAction.TaskStatusChanged)); Assert.Equal(2, await db.ActivityLogs.CountAsync(a => a.TaskId == long.Parse(id) && a.ActionType == ActivityAction.TaskUpdated));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskPatchAndStatusRejectEmptyUnknownDuplicateNullRequiredAndMalformedValues()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(owner, "Validation"))).GetProperty("id").GetString()!;
        var id = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/projects/" + project + "/tasks", new { title = "Retained" }))).GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + id;
        foreach (var body in new object[] { new { }, new { title = (string?)null }, new { title = " " }, new { title = 2 }, new { title = new string('x', 201) }, new { description = new string('x', 10001) }, new { description = false }, new { priority = (string?)null }, new { priority = "1" }, new { priority = 1 }, new { status = "Done" }, new { projectId = project }, new { assigneeId = 1 }, new { assigneeId = "0" }, new { assigneeId = long.MaxValue.ToString() }, new { dueDateUtc = "2026-01-01T00:00:00" }, new { dueDateUtc = 1 }, new { dueDateUtc = "0001-01-01T00:00:00Z" } })
            await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, body), HttpStatusCode.BadRequest);
        foreach (var raw in new[] { "[]", "null", "{\"title\":\"One\",\"title\":\"Two\"}", "{\"Title\":\"One\",\"title\":\"Two\"}" }) await Problem(await host.TaskRawAsync(owner, path, raw), HttpStatusCode.BadRequest);
        foreach (var body in new object[] { new { }, new { status = (string?)null }, new { status = "done" }, new { status = 1 }, new { status = "1" }, new { status = "Done", title = "Ignored?" } }) await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path + "/status", body), HttpStatusCode.BadRequest);
        Assert.Equal("Retained", (await Body(await owner.GetAsync(path))).GetProperty("title").GetString());
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task MembersCanEditAndChangeStatusButOnlyOwnerCanDeleteAndHistoryIsRetained()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1); using var outsider = await host.LoginAsync(2); using var anonymous = host.Client();
        var project = (await Body(await host.CreateAsync(owner, "Permissions"))).GetProperty("id").GetString()!; var root = "/api/v1/projects/" + project;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/members", new { userId = host.Users[1].Id.ToString() }));
        var id = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/tasks", new { title = "Task", assigneeId = host.Users[1].Id.ToString() }))).GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + id;
        foreach (var endpoint in new[] { path, path + "/status" })
        {
            object body = endpoint == path ? new { title = "Edited" } : new { status = "Done" };
            await Problem(await host.MemberWriteAsync(anonymous, HttpMethod.Patch, endpoint, body), HttpStatusCode.Unauthorized);
            await Problem(await host.MemberWriteAsync(outsider, HttpMethod.Patch, endpoint, body), HttpStatusCode.NotFound);
            using var noCsrf = new HttpRequestMessage(HttpMethod.Patch, endpoint) { Content = JsonContent.Create(body) }; await Problem(await owner.SendAsync(noCsrf), HttpStatusCode.BadRequest);
            await Body(await host.MemberWriteAsync(member, HttpMethod.Patch, endpoint, body));
        }
        Assert.Equal(100, (await Body(await owner.GetAsync(root))).GetProperty("progressPercentage").GetInt32());
        await Problem(await host.MemberWriteAsync(member, HttpMethod.Delete, path), HttpStatusCode.Forbidden); await Problem(await host.MemberWriteAsync(outsider, HttpMethod.Delete, path), HttpStatusCode.NotFound); await Problem(await host.MemberWriteAsync(anonymous, HttpMethod.Delete, path), HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.DeleteAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, path)).StatusCode);
        foreach (var client in new[] { owner, member }) await Problem(await client.GetAsync(path), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { title = "Resurrect" }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path + "/status", new { status = "ToDo" }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Delete, path), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Body(await owner.GetAsync(root + "/tasks"))).GetProperty("totalCount").GetInt32()); Assert.Equal(0, (await Body(await owner.GetAsync(root))).GetProperty("totalTasks").GetInt32());
        await using var db = fixture.CreateContext(); var task = await db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == long.Parse(id)); Assert.NotNull(task.DeletedAtUtc); Assert.Equal(host.Users[1].Id, task.AssigneeId);
        Assert.Equal(5, await db.ActivityLogs.CountAsync(a => a.TaskId == task.Id)); Assert.Single(await db.ActivityLogs.Where(a => a.TaskId == task.Id && a.ActionType == ActivityAction.TaskDeleted).ToListAsync());
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ReassignmentRecordsOldAndNewAssigneeAndRejectsNonMembersWithoutPartialEdits()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(owner, "Assign"))).GetProperty("id").GetString()!; var root = "/api/v1/projects/" + project;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/members", new { userId = host.Users[1].Id.ToString() }));
        var id = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/tasks", new { title = "Initial", assigneeId = host.Users[0].Id.ToString() }))).GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + id;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { assigneeId = host.Users[1].Id.ToString() }));
        var error = await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { title = "Must not change", assigneeId = host.Users[2].Id.ToString() }), HttpStatusCode.BadRequest); Assert.True(error.GetProperty("errors").TryGetProperty("assigneeId", out _));
        var task = await Body(await owner.GetAsync(path)); Assert.Equal("Initial", task.GetProperty("title").GetString()); Assert.Equal(host.Users[1].Id.ToString(), task.GetProperty("assigneeId").GetString());
        await using var db = fixture.CreateContext(); var events = await db.ActivityLogs.Where(a => a.TaskId == long.Parse(id)).OrderBy(a => a.Id).ToListAsync(); Assert.Equal(4, events.Count);
        Assert.Equal(ActivityAction.TaskUnassigned, events[2].ActionType); Assert.Equal(host.Users[0].Id.ToString(), JsonDocument.Parse(events[2].Details!).RootElement.GetProperty("userId").GetString()); Assert.Equal(ActivityAction.TaskAssigned, events[3].ActionType);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskEditStatusAndDeleteRollBackWhenActivityInsertionFails()
    {
        var failure = new FailActivity(); await using var host = new ProjectHost(fixture, failure); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(owner, "Rollback"))).GetProperty("id").GetString()!;
        var id = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/projects/" + project + "/tasks", new { title = "Retained" }))).GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + id;
        failure.Enabled = true;
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { title = "Lost", assigneeId = host.Users[0].Id.ToString() }), HttpStatusCode.InternalServerError);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path + "/status", new { status = "Done" }), HttpStatusCode.InternalServerError);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Delete, path), HttpStatusCode.InternalServerError);
        var task = await Body(await owner.GetAsync(path)); Assert.Equal("Retained", task.GetProperty("title").GetString()); Assert.Equal("ToDo", task.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, task.GetProperty("assigneeId").ValueKind);
        await using var db = fixture.CreateContext(); Assert.Equal(1, await db.ActivityLogs.CountAsync(a => a.TaskId == long.Parse(id)));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ConcurrentTaskEditAndDeletionSerializeWithoutResurrection()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var writer = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(owner, "Race"))).GetProperty("id").GetString()!;
        var id = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/projects/" + project + "/tasks", new { title = "Race" }))).GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + id;
        var results = await Task.WhenAll(host.MemberWriteAsync(writer, HttpMethod.Patch, path, new { title = "Edited" }), host.MemberWriteAsync(owner, HttpMethod.Delete, path));
        Assert.Contains(results[0].StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound }); Assert.Equal(HttpStatusCode.NoContent, results[1].StatusCode); await Problem(await owner.GetAsync(path), HttpStatusCode.NotFound);
        await using var db = fixture.CreateContext(); var events = await db.ActivityLogs.Where(a => a.TaskId == long.Parse(id)).OrderBy(a => a.Id).ToListAsync(); Assert.Equal(ActivityAction.TaskDeleted, events.Last().ActionType);
    }
}
