using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    private static async Task<HttpResponseMessage> RevisionWrite(HttpClient client, HttpMethod method, string path, string? version, object? body = null)
    {
        var csrf = (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf")).GetProperty("requestToken").GetString();
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add("X-CSRF-TOKEN", csrf);
        if (version is not null) request.Headers.TryAddWithoutValidation("X-Expected-Version", version);
        return await client.SendAsync(request);
    }
    [MySqlFact]
    public async Task ProjectRevisionRejectsStaleMissingAndInvalidInputsWithoutSuccessHistory()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); var path = W(c.A) + "/projects/" + c.A1.Id;
        var loaded = await Body(await owner.GetAsync(path)); var version = loaded.GetProperty("version").GetString();
        var same = await Body(await RevisionWrite(owner, HttpMethod.Put, path, version, new { name = c.A1.Name })); Assert.Equal(version, same.GetProperty("version").GetString());
        var saved = await Body(await RevisionWrite(owner, HttpMethod.Put, path, version, new { name = "First winner" })); Assert.NotEqual(version, saved.GetProperty("version").GetString());
        var stale = await Problem(await RevisionWrite(owner, HttpMethod.Put, path, version, new { name = "Lost draft" }), HttpStatusCode.Conflict);
        Assert.Equal("resource.conflict", stale.GetProperty("code").GetString()); Assert.False(stale.TryGetProperty("version", out _));
        await Problem(await RevisionWrite(owner, HttpMethod.Delete, path, version), HttpStatusCode.Conflict);
        await Problem(await RevisionWrite(owner, HttpMethod.Put, path, null, new { name = "Missing" }), (HttpStatusCode)428);
        foreach (var invalid in new[] { "0", "01", "-1", "1,2", "*", "9223372036854775808" }) await Problem(await RevisionWrite(owner, HttpMethod.Delete, path, invalid), HttpStatusCode.BadRequest);
        await using var db = fixture.CreateContext(); Assert.Equal("First winner", (await db.Projects.SingleAsync(p => p.Id == c.A1.Id)).Name);
        Assert.Equal(1, await db.ActivityLogs.CountAsync(a => a.ProjectId == c.A1.Id && a.ActionType == ActivityAction.ProjectUpdated));
    }
    [MySqlFact]
    public async Task RevisionChecksFollowAuthorizationAndDoNotRevealForeignOrDeletedResources()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2);
        foreach (var version in new string?[] { null, "1", "invalid" })
        {
            await Problem(await RevisionWrite(owner, HttpMethod.Put, W(c.A) + "/projects/" + c.B1.Id, version, new { name = "Foreign" }), HttpStatusCode.NotFound);
            await Problem(await RevisionWrite(admin, HttpMethod.Put, W(c.A) + "/projects/" + c.A1.Id, version, new { name = "Private" }), HttpStatusCode.NotFound);
            await Problem(await RevisionWrite(owner, HttpMethod.Patch, W(c.A) + "/tasks/" + c.TaskB.Id, version, new { title = "Foreign" }), HttpStatusCode.NotFound);
        }
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/members", new { userId = host.Users[2].Id.ToString(), role = "Viewer" });
        await Problem(await RevisionWrite(admin, HttpMethod.Put, W(c.A) + "/projects/" + c.A1.Id, "invalid", new { name = "Forbidden" }), HttpStatusCode.Forbidden);
        await host.MemberWriteAsync(owner, HttpMethod.Delete, W(c.A) + "/projects/" + c.A1.Id);
        await Problem(await RevisionWrite(owner, HttpMethod.Delete, W(c.A) + "/projects/" + c.A1.Id, "1"), HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task TaskRevisionCoversStatusDeletionAndAssignmentCleanupEvenWhenClockDoesNotAdvance()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var path = W(c.A) + "/tasks/" + c.TaskA.Id;
        var original = await Body(await owner.GetAsync(path)); var version = original.GetProperty("version").GetString();
        var changed = await Body(await RevisionWrite(owner, HttpMethod.Patch, path + "/status", version, new { status = "Done" })); Assert.NotEqual(version, changed.GetProperty("version").GetString());
        await Problem(await RevisionWrite(owner, HttpMethod.Patch, path, version, new { title = "Stale edit" }), HttpStatusCode.Conflict);
        await Problem(await RevisionWrite(owner, HttpMethod.Delete, path, version), HttpStatusCode.Conflict);
        var latest = changed.GetProperty("version").GetString();
        Assert.Equal(latest, (await Body(await RevisionWrite(owner, HttpMethod.Patch, path + "/status", latest, new { status = "Done" }))).GetProperty("version").GetString());
        await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/members", new { userId = host.Users[2].Id.ToString() });
        var assigned = await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { assigneeId = host.Users[2].Id.ToString() }));
        await host.MemberWriteAsync(owner, HttpMethod.Delete, W(c.A) + "/projects/" + c.A1.Id + "/members/" + host.Users[2].Id);
        var cleared = await Body(await owner.GetAsync(path)); Assert.NotEqual(assigned.GetProperty("version").GetString(), cleared.GetProperty("version").GetString());
        await Problem(await RevisionWrite(owner, HttpMethod.Patch, path, assigned.GetProperty("version").GetString(), new { assigneeId = host.Users[2].Id.ToString() }), HttpStatusCode.Conflict);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("assigneeId").ValueKind);
    }
    [MySqlFact]
    public async Task TwoConcurrentEditorsWithSameRevisionHaveExactlyOneWinnerAndOneAudit()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var one = await host.LoginAsync(0); using var two = await host.LoginAsync(0); var path = W(c.A) + "/projects/" + c.A1.Id;
        var version = (await Body(await one.GetAsync(path))).GetProperty("version").GetString();
        var results = await Task.WhenAll(RevisionWrite(one, HttpMethod.Put, path, version, new { name = "Tab one" }), RevisionWrite(two, HttpMethod.Put, path, version, new { name = "Tab two" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        await using var db = fixture.CreateContext(); Assert.Equal(1, await db.ActivityLogs.CountAsync(a => a.ProjectId == c.A1.Id && a.ActionType == ActivityAction.ProjectUpdated));
    }
    [MySqlFact]
    public async Task EfRevisionPredicateProtectsOtherTrackedWritersAndLegacyClientsCanOptIn()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        var project = await Body(await host.CreateAsync(owner, "Legacy")); var id = project.GetProperty("id").GetString(); var path = "/api/v1/projects/" + id;
        var changed = await Body(await RevisionWrite(owner, HttpMethod.Put, path, "1", new { name = "Guarded" }));
        await Problem(await RevisionWrite(owner, HttpMethod.Delete, path, "1"), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.OK, (await RevisionWrite(owner, HttpMethod.Put, path, null, new { name = "Compatible" })).StatusCode);
        await using var a = fixture.CreateContext(); await using var b = fixture.CreateContext();
        var first = await a.Projects.SingleAsync(p => p.Id.ToString() == id); var second = await b.Projects.SingleAsync(p => p.Id == first.Id);
        first.Description = "First"; await a.SaveChangesAsync(); second.Description = "Second"; await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync());
        Assert.Equal(JsonValueKind.String, changed.GetProperty("version").ValueKind);
    }
}
