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
    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivityReaderCoversMutationActionsRetainsDeletedTaskAndRemovedActorHistoryAndHasNoPublicWriter()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1); using var outsider = await host.LoginAsync(2); using var anonymous = host.Client();
        var project = (await Body(await host.CreateAsync(owner, "Original"))).GetProperty("id").GetString()!; var root = "/api/v1/projects/" + project;
        await Body(await host.WriteAsync(owner, HttpMethod.Put, project, "{\"name\":\"Renamed\"}"));
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/members", new { userId = host.Users[1].Id.ToString() }));
        var task = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/tasks", new { title = "Private title", description = "Secret note", assigneeId = host.Users[0].Id.ToString() }))).GetProperty("id").GetString()!; var path = "/api/v1/tasks/" + task;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { title = "Changed" })); await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { assigneeId = host.Users[1].Id.ToString() }));
        await Body(await host.MemberWriteAsync(member, HttpMethod.Patch, path + "/status", new { status = "Done" }));
        var feed = root + "/activity"; var before = await Body(await owner.GetAsync(feed));
        Assert.Equal(before.ToString(), (await Body(await member.GetAsync(feed))).ToString());
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { title = " Changed " })); await Body(await host.MemberWriteAsync(member, HttpMethod.Patch, path + "/status", new { status = "Done" }));
        Assert.Equal(before.GetProperty("totalCount").GetInt32(), (await Body(await owner.GetAsync(feed))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, path)).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, root + "/members/" + host.Users[1].Id)).StatusCode);
        var response = await owner.GetAsync(feed); Assert.True(response.Headers.CacheControl?.NoStore); var items = (await Body(response)).GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(Enum.GetNames<ActivityAction>().Where(a => a is not ("ProjectDeleted" or "MemberRoleChanged" or "ProjectOwnershipTransferred")).Order(), items.Select(a => a.GetProperty("actionType").GetString()).Distinct().Order());
        Assert.Contains(items, a => a.GetProperty("actorUserId").GetString() == host.Users[1].Id.ToString()); Assert.Contains(items, a => a.GetProperty("actionType").GetString() == "TaskDeleted" && a.GetProperty("taskId").GetString() == task);
        Assert.Contains(items, a => a.GetProperty("details").GetString() == "Status changed from To Do to Completed."); Assert.All(items, a => { Assert.Equal(7, a.EnumerateObject().Count()); Assert.Equal(project, a.GetProperty("projectId").GetString()); Assert.EndsWith("Z", a.GetProperty("createdAtUtc").GetString()); Assert.DoesNotContain("Secret note", a.ToString()); Assert.DoesNotContain("Private title", a.ToString()); });
        foreach (var client in new[] { member, outsider, anonymous }) await Problem(await client.GetAsync(feed), client == anonymous ? HttpStatusCode.Unauthorized : HttpStatusCode.NotFound);
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete }) { var rejected = await host.MemberWriteAsync(owner, method, feed, new { details = "Forged" }); Assert.Contains(rejected.StatusCode, new[] { HttpStatusCode.MethodNotAllowed, HttpStatusCode.NotFound }); }
        Assert.Equal(items.Length, (await Body(await owner.GetAsync(feed))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await host.WriteAsync(owner, HttpMethod.Delete, project)).StatusCode); await Problem(await owner.GetAsync(feed), HttpStatusCode.NotFound);
        await using var db = fixture.CreateContext(); Assert.True(await db.ActivityLogs.IgnoreQueryFilters().AnyAsync(a => a.ProjectId == long.Parse(project) && a.ActionType == ActivityAction.ProjectDeleted));
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivityRangesAreInclusiveOffsetAwareAndPreserveMicrosecondBoundariesAndLargeIds()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = long.Parse((await Body(await host.CreateAsync(owner, "Ranges"))).GetProperty("id").GetString()!); var path = "/api/v1/projects/" + project + "/activity";
        const long large = 9_007_199_254_741_777; host.Clock.Now = host.Clock.Now.AddMinutes(1);
        await using (var db = fixture.CreateContext(host.Clock)) { db.ActivityLogs.Add(new ActivityLog { Id = large, ProjectId = project, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.ProjectUpdated }); await db.SaveChangesAsync(); }
        var stamp = host.Clock.Now.UtcDateTime; var lower = stamp.ToString("O"); var offset = host.Clock.Now.ToOffset(TimeSpan.FromHours(5.5)).ToString("O");
        var exact = await Body(await owner.GetAsync(path + "?fromUtc=" + Uri.EscapeDataString(offset) + "&toUtc=" + Uri.EscapeDataString(lower))); Assert.Equal(1, exact.GetProperty("totalCount").GetInt32()); Assert.Equal(large.ToString(), exact.GetProperty("items")[0].GetProperty("id").GetString());
        var after = await Body(await owner.GetAsync(path + "?fromUtc=" + Uri.EscapeDataString(stamp.AddTicks(1).ToString("O")))); Assert.Equal(0, after.GetProperty("totalCount").GetInt32());
        var before = await Body(await owner.GetAsync(path + "?fromUtc=" + Uri.EscapeDataString(stamp.AddTicks(-1).ToString("O")) + "&toUtc=" + Uri.EscapeDataString(stamp.AddTicks(-1).ToString("O")))); Assert.Equal(0, before.GetProperty("totalCount").GetInt32());
        var tooLate = await Body(await owner.GetAsync(path + "?fromUtc=9999-12-31T23:59:59.9999999Z")); Assert.Equal(0, tooLate.GetProperty("totalCount").GetInt32());
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivityQueriesHaveStableTimestampAndNumericIdPagingAndRejectInvalidBoundsAndParameters()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = long.Parse((await Body(await host.CreateAsync(owner, "Paging"))).GetProperty("id").GetString()!); var path = "/api/v1/projects/" + project + "/activity";
        var rows = new List<ActivityLog>(); await using (var db = fixture.CreateContext(host.Clock)) { for (var i = 0; i < 22; i++) { var row = new ActivityLog { ProjectId = project, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.ProjectUpdated }; rows.Add(row); db.ActivityLogs.Add(row); } await db.SaveChangesAsync(); }
        var ids = new List<long>(); for (var page = 1; page <= 3; page++) { var result = await Body(await owner.GetAsync(path + $"?page={page}&pageSize=10")); Assert.Equal(23, result.GetProperty("totalCount").GetInt32()); ids.AddRange(result.GetProperty("items").EnumerateArray().Select(a => long.Parse(a.GetProperty("id").GetString()!))); }
        Assert.Equal(ids.OrderDescending(), ids); Assert.Equal(23, ids.Distinct().Count());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "?page=99"))).GetProperty("items").GetArrayLength());
        foreach (var query in new[] { "sortBy=id", "sortDirection=asc", "unknown=1", "page=0", "pageSize=101", "page=2147483647&pageSize=100", "fromUtc=", "toUtc=", "fromUtc=2026-01-01", "toUtc=2026-01-01T00:00:00", "fromUtc=0001-01-01T00:00:00Z", "fromUtc=2026-11-02T00:00:00Z&toUtc=2026-11-01T00:00:00Z", "fromUtc=2026-01-01T00:00:00Z&fromUtc=2026-01-01T00:00:00Z", "toUtc=2026-01-01T00:00:00Z&toUtc=2026-01-01T00:00:00Z", "page=1&page=1", "pageSize=20&pageSize=20" }) await Problem(await owner.GetAsync(path + "?" + query), HttpStatusCode.BadRequest);
        foreach (var id in new[] { "0", "-1", "9223372036854775808", "abc" }) await Problem(await owner.GetAsync("/api/v1/projects/" + id + "/activity"), HttpStatusCode.BadRequest);
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivitySummariesNeverExposeArbitraryStoredPayloadsOrOtherProjectRows()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = long.Parse((await Body(await host.CreateAsync(owner, "Safe"))).GetProperty("id").GetString()!); var other = long.Parse((await Body(await host.CreateAsync(owner, "Other"))).GetProperty("id").GetString()!);
        await using (var db = fixture.CreateContext()) { db.ActivityLogs.AddRange(new ActivityLog { ProjectId = project, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.TaskStatusChanged, Details = "{\"from\":\"<script>secret</script>\",\"to\":\"Done\",\"email\":\"private@example.test\"}" }, new ActivityLog { ProjectId = project, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.MemberAdded, Details = "not-json secret" }, new ActivityLog { ProjectId = other, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.ProjectUpdated }); await db.SaveChangesAsync(); }
        var body = await Body(await owner.GetAsync("/api/v1/projects/" + project + "/activity")); Assert.Equal(3, body.GetProperty("totalCount").GetInt32()); Assert.DoesNotContain("secret", body.ToString()); Assert.DoesNotContain("private@example.test", body.ToString()); Assert.Contains(body.GetProperty("items").EnumerateArray(), a => a.GetProperty("details").GetString() == "Task status changed.");
    }
    private sealed class ActivityCountGate : DbCommandInterceptor
    {
        public bool Armed { get; set; } public TaskCompletionSource Counted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default) { if (Armed && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal) && command.CommandText.Contains("`ActivityLogs`", StringComparison.Ordinal)) { Counted.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); } return result; }
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivityCountAndRowsUseOneSnapshotDuringAConcurrentProjectMutation()
    {
        var gate = new ActivityCountGate(); await using var host = new ProjectHost(fixture, gate); await host.InitializeAsync(); using var reader = await host.LoginAsync(0); using var writer = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(writer, "Snapshot"))).GetProperty("id").GetString()!; var path = "/api/v1/projects/" + project + "/activity";
        gate.Armed = true; var pending = reader.GetAsync(path); try { await gate.Counted.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Body(await host.WriteAsync(writer, HttpMethod.Put, project, "{\"name\":\"Concurrent change\"}")); } finally { gate.Armed = false; gate.Release.TrySetResult(); }
        var snapshot = await Body(await pending.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(1, snapshot.GetProperty("totalCount").GetInt32()); Assert.Equal(1, snapshot.GetProperty("items").GetArrayLength()); Assert.Equal(2, (await Body(await reader.GetAsync(path))).GetProperty("totalCount").GetInt32());
    }
}
