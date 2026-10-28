using System.Net;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact]
    public async Task WorkspaceWritesDefaultOffAndEnabledWritesRequireCsrfWithServerDerivedScope()
    {
        await using (var disabled = new ProjectHost(fixture, true))
        {
            await disabled.InitializeAsync(); var c = await SeedCompanies(disabled); using var user = await disabled.LoginAsync(0);
            await Problem(await disabled.MemberWriteAsync(user, HttpMethod.Post, W(c.A) + "/projects", new { name = "Off" }), HttpStatusCode.NotFound);
        }
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync(); var companies = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var anonymous = host.Client();
        await Problem(await host.MemberWriteAsync(anonymous, HttpMethod.Post, W(companies.A) + "/projects", new { name = "No login" }), HttpStatusCode.Unauthorized);
        await Problem(await owner.PostAsync(W(companies.A) + "/projects", System.Net.Http.Json.JsonContent.Create(new { name = "Missing CSRF" })), HttpStatusCode.BadRequest);
        var response = await host.MemberWriteAsync(owner, HttpMethod.Post, W(companies.A) + "/projects", new { name = "Scoped project" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); var body = await Body(response); var project = long.Parse(body.GetProperty("id").GetString()!);
        Assert.Contains(W(companies.A), response.Headers.Location!.OriginalString);
        await using var db = fixture.CreateContext();
        Assert.Equal(companies.A.Id, (await db.Projects.SingleAsync(p => p.Id == project)).WorkspaceId);
        var activity = await db.ActivityLogs.SingleAsync(a => a.ProjectId == project); Assert.Equal(companies.A.Id, activity.WorkspaceId); Assert.Equal(host.Users[0].Id, activity.ActorUserId);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(companies.A) + "/projects", new { name = "Cannot inject", workspaceId = companies.B.Id.ToString() }), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(companies.A) + "/projects?workspaceId=" + companies.B.Id, new { name = "No query override" }), HttpStatusCode.BadRequest);
    }

    [MySqlFact]
    public async Task WorkspaceProjectTaskAndMemberCrudKeepEveryNewRowAndEventInOneCompany()
    {
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2);
        var project = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "New" }))).GetProperty("id").GetString()!;
        var path = W(c.A) + "/projects/" + project;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Put, path, new { name = "Renamed", description = "Context" }));
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/members", new { userId = host.Users[2].Id.ToString() }));
        var taskResponse = await host.MemberWriteAsync(admin, HttpMethod.Post, path + "/tasks", new { title = "Scoped task", assigneeId = host.Users[2].Id.ToString() });
        var task = await Body(taskResponse); var taskId = task.GetProperty("id").GetString()!; var taskPath = W(c.A) + "/tasks/" + taskId;
        Assert.Contains(W(c.A), taskResponse.Headers.Location!.OriginalString);
        await Body(await host.MemberWriteAsync(admin, HttpMethod.Patch, taskPath, new { title = "Edited", description = "Details", priority = "High" }));
        await Body(await host.MemberWriteAsync(admin, HttpMethod.Patch, taskPath + "/status", new { status = "Done" }));
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Delete, taskPath), HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, path + "/members/" + host.Users[2].Id)).StatusCode);
        await using (var db = fixture.CreateContext())
        {
            var row = await db.Tasks.SingleAsync(t => t.Id == long.Parse(taskId)); Assert.Null(row.AssigneeId); Assert.Equal(c.A.Id, row.WorkspaceId);
            var events = await db.ActivityLogs.Where(a => a.ProjectId == long.Parse(project)).ToArrayAsync(); Assert.All(events, e => Assert.Equal(c.A.Id, e.WorkspaceId));
            Assert.Contains(events, e => e.ActionType == ActivityAction.TaskUnassigned);
        }
        await Problem(await admin.GetAsync(taskPath), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, taskPath)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, path)).StatusCode);
        await Problem(await owner.GetAsync(path), HttpStatusCode.NotFound);
    }

    [MySqlFact]
    public async Task MixedCompanyWritesGuestCreationAndCompanyAdminPrivateOverridesAreDenied()
    {
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var shared = await host.LoginAsync(0); using var admin = await host.LoginAsync(2); using var ownerB = await host.LoginAsync(1);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Post, W(c.B) + "/projects", new { name = "Guest cannot create" }), HttpStatusCode.Forbidden);
        foreach (var method in new[] { HttpMethod.Put, HttpMethod.Delete })
            await Problem(await host.MemberWriteAsync(admin, method, W(c.A) + "/projects/" + c.A1.Id, method == HttpMethod.Put ? new { name = "No admin bypass" } : null), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Post, W(c.A) + "/projects/" + c.B1.Id + "/tasks", new { title = "Crossed" }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Put, W(c.A) + "/projects/" + c.B1.Id, new { name = "Crossed" }), HttpStatusCode.NotFound);
        foreach (var method in new[] { HttpMethod.Patch, HttpMethod.Delete })
            await Problem(await host.MemberWriteAsync(shared, method, W(c.A) + "/tasks/" + c.TaskB.Id, method == HttpMethod.Patch ? new { title = "Crossed" } : null), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Patch, W(c.A) + "/tasks/" + c.TaskB.Id + "/status", new { status = "Done" }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/members", new { userId = host.Users[1].Id.ToString() }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/tasks", new { title = "Foreign assignee", assigneeId = host.Users[1].Id.ToString() }), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Patch, W(c.A) + "/tasks/" + c.TaskA.Id, new { assigneeId = host.Users[1].Id.ToString() }), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(shared, HttpMethod.Delete, W(c.B) + "/projects/" + c.B1.Id), HttpStatusCode.Forbidden);
        await using var db = fixture.CreateContext(); Assert.Equal("B shared", (await db.Projects.SingleAsync(p => p.Id == c.B1.Id)).Name);
        Assert.Equal(TaskState.InProgress, (await db.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).Status);
        Assert.False(await db.ActivityLogs.AnyAsync(a => a.ProjectId == c.A1.Id && a.ActionType != ActivityAction.TaskCreated));
    }

    [MySqlFact]
    public async Task ScopedPatchPreservesOmissionClearsNullsAndRejectsInjectedOrDuplicateProperties()
    {
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var path = W(c.A) + "/tasks/" + c.TaskA.Id;
        var before = await Body(await owner.GetAsync(path));
        var edited = await Body(await host.TaskRawAsync(owner, path, "{\"title\":\"Trimmed\"}"));
        Assert.Equal(before.GetProperty("dueDateUtc").GetString(), edited.GetProperty("dueDateUtc").GetString());
        Assert.Equal(host.Users[0].Id.ToString(), edited.GetProperty("assigneeId").GetString());
        var cleared = await Body(await host.TaskRawAsync(owner, path, "{\"description\":null,\"assigneeId\":null,\"dueDateUtc\":null}"));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, cleared.GetProperty("assigneeId").ValueKind);
        foreach (var body in new[] { "{}", "{\"title\":null}", "{\"priority\":1}", "{\"title\":\"A\",\"title\":\"B\"}", "{\"workspaceId\":\"1\"}", "{\"projectId\":\"1\"}", "{\"assigneeId\":1}" })
            await Problem(await host.TaskRawAsync(owner, path, body), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path + "/status", new { status = "1" }), HttpStatusCode.BadRequest);
        await using var db = fixture.CreateContext(); Assert.Equal("Trimmed", (await db.Tasks.SingleAsync(t => t.Id == c.TaskA.Id)).Title);
    }

    [MySqlFact]
    public async Task ScopedRemovalAndCompetingAssignmentsSerializeInBothOrders()
    {
        foreach (var removalFirst in new[] { true, false })
        {
            var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, true, true, gate); await host.InitializeAsync(); var c = await SeedCompanies(host);
            using var owner = await host.LoginAsync(1); using var writer = await host.LoginAsync(1); using var member = await host.LoginAsync(0);
            await using (var db = fixture.CreateContext()) { var row = await db.Tasks.SingleAsync(t => t.Id == c.TaskB.Id); row.AssigneeId = null; await db.SaveChangesAsync(); }
            var project = W(c.B) + "/projects/" + c.B1.Id; var task = W(c.B) + "/tasks/" + c.TaskB.Id;
            Task<HttpResponseMessage> Remove() => host.MemberWriteAsync(owner, HttpMethod.Delete, project + "/members/" + host.Users[0].Id);
            Task<HttpResponseMessage> Assign() => host.MemberWriteAsync(writer, HttpMethod.Patch, task, new { assigneeId = host.Users[0].Id.ToString() });
            var result = removalFirst ? await gate.OrderedAsync(Remove, Assign) : await gate.OrderedAsync(Assign, Remove);
            Assert.Equal(removalFirst ? HttpStatusCode.NoContent : HttpStatusCode.OK, result.First.StatusCode);
            Assert.Equal(removalFirst ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent, result.Second.StatusCode);
            await using var check = fixture.CreateContext(); Assert.Null((await check.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).AssigneeId);
            Assert.False(await check.ProjectMembers.AnyAsync(m => m.ProjectId == c.B1.Id && m.UserId == host.Users[0].Id && m.RevokedAtUtc == null));
            await Problem(await host.MemberWriteAsync(member, HttpMethod.Post, project + "/tasks", new { title = "Removed member" }), HttpStatusCode.NotFound);
        }
    }

    [MySqlFact]
    public async Task FailedScopedActivityRollsBackMemberRemovalAndAssignmentCleanup()
    {
        var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, true, true, gate, new FailRemovalActivity()); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(1); using var writer = await host.LoginAsync(1);
        var project = W(c.B) + "/projects/" + c.B1.Id; var task = W(c.B) + "/tasks/" + c.TaskB.Id;
        var result = await gate.OrderedAsync(() => host.MemberWriteAsync(owner, HttpMethod.Delete, project + "/members/" + host.Users[0].Id),
            () => host.MemberWriteAsync(writer, HttpMethod.Patch, task, new { title = "After rollback" }));
        await Problem(result.First, HttpStatusCode.InternalServerError); Assert.Equal(HttpStatusCode.OK, result.Second.StatusCode);
        await using var db = fixture.CreateContext(); Assert.Equal(host.Users[0].Id, (await db.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).AssigneeId);
        Assert.True(await db.ProjectMembers.AnyAsync(m => m.ProjectId == c.B1.Id && m.UserId == host.Users[0].Id && m.RevokedAtUtc == null));
        Assert.False(await db.ActivityLogs.AnyAsync(a => a.ProjectId == c.B1.Id && (a.ActionType == ActivityAction.MemberRemoved || a.ActionType == ActivityAction.TaskUnassigned)));
    }

    [MySqlFact]
    public async Task SuspendedWorkspaceActorsAndAssigneesCannotUseExistingProjectGrants()
    {
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(2);
        var project = W(c.A) + "/projects/" + c.A1.Id;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, project + "/members", new { userId = host.Users[2].Id.ToString() }));
        await using (var db = fixture.CreateContext())
        {
            var membership = await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[2].Id);
            membership.State = WorkspaceMembershipState.Suspended; await db.SaveChangesAsync();
        }
        await Problem(await host.MemberWriteAsync(member, HttpMethod.Post, project + "/tasks", new { title = "Suspended" }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, project + "/tasks", new { title = "Inactive assignee", assigneeId = host.Users[2].Id.ToString() }), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, W(c.A) + "/tasks/" + c.TaskA.Id, new { assigneeId = host.Users[2].Id.ToString() }), HttpStatusCode.BadRequest);
        await using var check = fixture.CreateContext(); Assert.Equal(host.Users[0].Id, (await check.Tasks.SingleAsync(t => t.Id == c.TaskA.Id)).AssigneeId);
        Assert.False(await check.Tasks.AnyAsync(t => t.Title == "Suspended" || t.Title == "Inactive assignee"));
    }

    [MySqlFact]
    public async Task QueuedScopedWriteRechecksActorMembershipAfterTheWorkspaceLock()
    {
        var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, true, true, gate); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(1); using var member = await host.LoginAsync(0);
        gate.Armed = true; var first = host.MemberWriteAsync(owner, HttpMethod.Put, W(c.B) + "/projects/" + c.B1.Id, new { name = "Lock holder" });
        Task<HttpResponseMessage>? second = null;
        try
        {
            await gate.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = host.MemberWriteAsync(member, HttpMethod.Post, W(c.B) + "/projects/" + c.B1.Id + "/tasks", new { title = "Queued stale actor" });
            await gate.Queued.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var db = fixture.CreateContext(); db.Database.SetCommandTimeout(5);
            var membership = await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.B.Id && m.UserId == host.Users[0].Id);
            membership.State = WorkspaceMembershipState.Suspended; await db.SaveChangesAsync();
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.OK, (await first.WaitAsync(TimeSpan.FromSeconds(15))).StatusCode);
        await Problem(await second!.WaitAsync(TimeSpan.FromSeconds(15)), HttpStatusCode.NotFound);
        await using var check = fixture.CreateContext(); Assert.False(await check.Tasks.AnyAsync(t => t.Title == "Queued stale actor"));
    }

    [MySqlFact]
    public async Task CompositeForeignKeysRejectCrossCompanyAndCrossProjectReferencesOutsideServices()
    {
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        var invalid = new object[]
        {
            new Project { WorkspaceId = c.A.Id, OwnerId = host.Users[1].Id, Name = "Foreign owner" },
            new ProjectMember { WorkspaceId = c.B.Id, ProjectId = c.A1.Id, UserId = host.Users[1].Id },
            new ProjectMember { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, UserId = host.Users[1].Id },
            new TaskItem { WorkspaceId = c.A.Id, ProjectId = c.B1.Id, Title = "Wrong parent" },
            new ActivityLog { WorkspaceId = c.A.Id, ProjectId = c.B1.Id, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.ProjectUpdated },
            new ActivityLog { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, TaskId = c.TaskB.Id, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.TaskUpdated }
        };
        foreach (var entity in invalid)
        {
            await using var db = fixture.CreateContext(); db.Add(entity);
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using var context = fixture.CreateContext(); await context.Database.OpenConnectionAsync();
        await using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA = DATABASE()";
        var names = new HashSet<string>(); await using var reader = await command.ExecuteReaderAsync(); while (await reader.ReadAsync()) names.Add(reader.GetString(0));
        foreach (var name in new[] { "FK_Projects_WorkspaceMembers_Owner", "FK_ProjectMembers_Projects_Workspace", "FK_ProjectMembers_WorkspaceMembers_User", "FK_Tasks_Projects_Workspace", "FK_ActivityLogs_Projects_Workspace", "FK_ActivityLogs_Tasks_WorkspaceProject" }) Assert.Contains(name, names);
    }
}
