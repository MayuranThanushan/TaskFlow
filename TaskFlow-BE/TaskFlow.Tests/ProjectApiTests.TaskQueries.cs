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
    public async Task EveryTaskSortHasDeterministicNumericIdTiesAcrossPagesAndNullDeadlinesLast()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = long.Parse((await Body(await host.CreateAsync(owner, "Sorts"))).GetProperty("id").GetString()!); var path = "/api/v1/projects/" + project + "/tasks";
        var one = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc); var two = one.AddDays(1);
        TaskItem[] rows = [new() { Title = "Alpha", Priority = TaskPriority.High, DueDateUtc = two }, new() { Title = "Alpha", Priority = TaskPriority.Low, DueDateUtc = one }, new() { Title = "Beta", Priority = TaskPriority.Medium, DueDateUtc = one }, new() { Title = "Beta", Priority = TaskPriority.High }, new() { Title = "Gamma", Priority = TaskPriority.Medium }, new() { Title = "Gamma", Priority = TaskPriority.Low, DueDateUtc = two }];
        await using (var db = fixture.CreateContext(host.Clock))
        {
            for (var group = 0; group < 3; group++) { foreach (var row in rows.Skip(group * 2).Take(2)) { row.ProjectId = project; db.Tasks.Add(row); } await db.SaveChangesAsync(); host.Clock.Now = host.Clock.Now.AddSeconds(1); }
        }
        (string Sort, string Direction, int[] Order)[] orders = [
            ("title", "asc", [1, 0, 3, 2, 5, 4]), ("title", "desc", [5, 4, 3, 2, 1, 0]),
            ("priority", "asc", [5, 1, 4, 2, 3, 0]), ("priority", "desc", [3, 0, 4, 2, 5, 1]),
            ("dueDateUtc", "asc", [2, 1, 5, 0, 4, 3]), ("dueDateUtc", "desc", [5, 0, 2, 1, 4, 3]),
            ("createdAtUtc", "asc", [1, 0, 3, 2, 5, 4]), ("createdAtUtc", "desc", [5, 4, 3, 2, 1, 0])];
        foreach (var order in orders)
        {
            var ids = new List<string>();
            for (var page = 1; page <= 3; page++)
            {
                var body = await Body(await owner.GetAsync(path + $"?sortBy={order.Sort}&sortDirection={order.Direction}&pageSize=2&page={page}"));
                Assert.Equal(6, body.GetProperty("totalCount").GetInt32()); Assert.Equal(3, body.GetProperty("totalPages").GetInt32());
                ids.AddRange(body.GetProperty("items").EnumerateArray().Select(t => t.GetProperty("id").GetString()!));
            }
            Assert.Equal(order.Order.Select(index => rows[index].Id.ToString()), ids); Assert.Equal(6, ids.Distinct().Count());
        }
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task CombinedTaskFiltersRemainLiteralAndScopedBeforeCountsAndAfterMembershipRemoval()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1); using var outsider = await host.LoginAsync(2);
        var project = long.Parse((await Body(await host.CreateAsync(owner, "Filters"))).GetProperty("id").GetString()!); var root = "/api/v1/projects/" + project;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, root + "/members", new { userId = host.Users[1].Id.ToString() }));
        var other = long.Parse((await Body(await host.CreateAsync(owner, "Other scope"))).GetProperty("id").GetString()!);
        TaskItem Match() => new() { ProjectId = project, Title = "100%_Ready\\Go", Status = TaskState.Done, Priority = TaskPriority.High, AssigneeId = host.Users[1].Id };
        var wanted = Match(); var status = Match(); status.Status = TaskState.ToDo; var priority = Match(); priority.Priority = TaskPriority.Medium;
        var assignee = Match(); assignee.AssigneeId = host.Users[0].Id; var title = Match(); title.Title = "100XXReady\\Go";
        var deleted = Match(); deleted.DeletedAtUtc = DateTime.UtcNow; var otherScope = Match(); otherScope.ProjectId = other; var unassigned = Match(); unassigned.AssigneeId = null;
        await using (var db = fixture.CreateContext()) { db.Tasks.AddRange(wanted, status, priority, assignee, title, deleted, otherScope, unassigned); await db.SaveChangesAsync(); }
        var query = root + "/tasks?search=" + Uri.EscapeDataString(" %_ready\\go ") + "&status=Done&priority=High&assigneeId=" + host.Users[1].Id;
        foreach (var client in new[] { owner, member }) { var result = await Body(await client.GetAsync(query)); Assert.Equal(1, result.GetProperty("totalCount").GetInt32()); Assert.Equal(wanted.Id.ToString(), result.GetProperty("items")[0].GetProperty("id").GetString()); }
        Assert.Equal(6, (await Body(await owner.GetAsync(root + "/tasks"))).GetProperty("totalCount").GetInt32()); await Problem(await outsider.GetAsync(query), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, root + "/members/" + host.Users[1].Id)).StatusCode);
        var empty = await Body(await owner.GetAsync(query)); Assert.Equal(0, empty.GetProperty("totalCount").GetInt32()); Assert.Equal(0, empty.GetProperty("totalPages").GetInt32()); await Problem(await member.GetAsync(query), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await host.WriteAsync(owner, HttpMethod.Delete, project.ToString())).StatusCode); await Problem(await owner.GetAsync(query), HttpStatusCode.NotFound);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskQueryRejectsEachRepeatedParameterAndMalformedFilterAndSortValues()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = (await Body(await host.CreateAsync(owner, "Invalid queries"))).GetProperty("id").GetString()!; var path = "/api/v1/projects/" + project + "/tasks?";
        foreach (var pair in new[] { "page=1", "pageSize=20", "search=Task", "status=ToDo", "priority=High", "assigneeId=" + host.Users[0].Id, "sortBy=title", "sortDirection=asc" }) await Problem(await owner.GetAsync(path + pair + "&" + pair), HttpStatusCode.BadRequest);
        foreach (var query in new[] { "sortBy=Title", "sortBy=title%20desc", "sortDirection=ASC", "status=ToDo,Done", "priority=Low,High", "assigneeId=-1", "assigneeId=1.0", "assigneeId=null", "assigneeId=9223372036854775808", "status=", "priority=", "assigneeId=", "sortBy=", "sortDirection=", "pageSize=0", "page=2147483648", "pageSize=2147483648", "page=2147483647&pageSize=2", "search=" + new string('x', 201), "ownerId=1", "page=1&Page=2" }) await Problem(await owner.GetAsync(path + query), HttpStatusCode.BadRequest);
    }

    private sealed class TaskCountGate : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Counted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("COUNT(*)", StringComparison.Ordinal) && command.CommandText.Contains("`Tasks`", StringComparison.Ordinal))
            { Counted.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken); }
            return result;
        }
    }
    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskCountAndPageShareASnapshotWhileAnotherRequestCreatesATask()
    {
        var gate = new TaskCountGate(); await using var host = new ProjectHost(fixture, gate); await host.InitializeAsync(); using var reader = await host.LoginAsync(0); using var writer = await host.LoginAsync(0);
        var project = long.Parse((await Body(await host.CreateAsync(writer, "Snapshot"))).GetProperty("id").GetString()!); var path = "/api/v1/projects/" + project + "/tasks";
        await using (var db = fixture.CreateContext(host.Clock)) { for (var i = 0; i < 21; i++) db.Tasks.Add(new TaskItem { ProjectId = project, Title = "Initial " + i }); await db.SaveChangesAsync(); }
        gate.Armed = true; var pending = reader.GetAsync(path); string createdId;
        try { await gate.Counted.Task.WaitAsync(TimeSpan.FromSeconds(10)); createdId = (await Body(await host.MemberWriteAsync(writer, HttpMethod.Post, path, new { title = "Concurrent" }))).GetProperty("id").GetString()!; }
        finally { gate.Release.TrySetResult(); gate.Armed = false; }
        var snapshot = await Body(await pending.WaitAsync(TimeSpan.FromSeconds(10))); Assert.Equal(21, snapshot.GetProperty("totalCount").GetInt32()); Assert.Equal(20, snapshot.GetProperty("items").GetArrayLength()); Assert.DoesNotContain(snapshot.GetProperty("items").EnumerateArray(), t => t.GetProperty("id").GetString() == createdId);
        var fresh = await Body(await reader.GetAsync(path)); Assert.Equal(22, fresh.GetProperty("totalCount").GetInt32()); Assert.Equal(createdId, fresh.GetProperty("items")[0].GetProperty("id").GetString());
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task TaskMaximumPageSizeAndEmptyAndOverflowBoundariesPreserveRealMetadata()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = long.Parse((await Body(await host.CreateAsync(owner, "Bounds"))).GetProperty("id").GetString()!); var path = "/api/v1/projects/" + project + "/tasks";
        await using (var db = fixture.CreateContext()) { for (var i = 0; i < 101; i++) db.Tasks.Add(new TaskItem { ProjectId = project, Title = "Task " + i }); await db.SaveChangesAsync(); }
        var first = await Body(await owner.GetAsync(path + "?pageSize=100")); Assert.Equal(100, first.GetProperty("items").GetArrayLength()); Assert.Equal(101, first.GetProperty("totalCount").GetInt32()); Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        var second = await Body(await owner.GetAsync(path + "?pageSize=100&page=2")); Assert.Equal(1, second.GetProperty("items").GetArrayLength());
        var far = await Body(await owner.GetAsync(path + "?page=2147483647&pageSize=1")); Assert.Equal(0, far.GetProperty("items").GetArrayLength()); Assert.Equal(101, far.GetProperty("totalCount").GetInt32());
        var empty = await Body(await owner.GetAsync(path + "?search=NoSuchTitle&page=7&pageSize=100")); Assert.Equal(0, empty.GetProperty("items").GetArrayLength()); Assert.Equal(0, empty.GetProperty("totalPages").GetInt32()); Assert.Equal(7, empty.GetProperty("page").GetInt32());
    }
}
