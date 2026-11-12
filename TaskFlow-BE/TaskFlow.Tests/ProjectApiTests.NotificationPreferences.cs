using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact]
    public async Task NotificationPreferencesAreSelfCompanyScopedWithDefaultsAndConflictProtection()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); using var recipient = await host.LoginAsync(2); using var second = await host.LoginAsync(2); using var owner = await host.LoginAsync(0);
        var path = W(c.A) + "/notifications/preferences"; var initial = await Body(await recipient.GetAsync(path));
        Assert.True(initial.GetProperty("inAppEnabled").GetBoolean()); Assert.True(initial.GetProperty("taskAssignedEnabled").GetBoolean()); Assert.False(initial.GetProperty("emailAvailable").GetBoolean()); Assert.Equal("0", initial.GetProperty("version").GetString());
        var results = await Task.WhenAll(host.MemberWriteAsync(recipient, HttpMethod.Put, path, new { inAppEnabled = false, taskAssignedEnabled = true, expectedVersion = "0" }), host.MemberWriteAsync(second, HttpMethod.Put, path, new { inAppEnabled = true, taskAssignedEnabled = false, expectedVersion = "0" }));
        Assert.Single(results, r => r.IsSuccessStatusCode); var conflict = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("resource.conflict", (await Problem(conflict, HttpStatusCode.Conflict)).GetProperty("code").GetString());
        var current = await Body(await recipient.GetAsync(path)); Assert.Equal("1", current.GetProperty("version").GetString());
        Assert.NotEqual(current.GetProperty("inAppEnabled").GetBoolean(), current.GetProperty("taskAssignedEnabled").GetBoolean());
        Assert.Equal("0", (await Body(await owner.GetAsync(path))).GetProperty("version").GetString());
        await using (var db = fixture.CreateContext())
        {
            db.WorkspaceMemberships.Add(new() { WorkspaceId = c.B.Id, UserId = host.Users[2].Id, Role = WorkspaceRole.Member }); await db.SaveChangesAsync();
            Assert.Single(await db.NotificationPreferences.Where(p => p.WorkspaceId == c.A.Id).ToArrayAsync());
        }
        Assert.Equal("0", (await Body(await recipient.GetAsync(W(c.B) + "/notifications/preferences"))).GetProperty("version").GetString());
        var saved = await Body(await host.MemberWriteAsync(recipient, HttpMethod.Put, path, new { inAppEnabled = true, taskAssignedEnabled = true, expectedVersion = "1" }));
        Assert.Equal("2", saved.GetProperty("version").GetString());
        var repeated = await Body(await host.MemberWriteAsync(recipient, HttpMethod.Put, path, new { inAppEnabled = true, taskAssignedEnabled = true, expectedVersion = "2" })); Assert.Equal("2", repeated.GetProperty("version").GetString());
    }

    [MySqlFact]
    public async Task NotificationMutedChannelsAndEventsSuppressOnlyFutureAssignments()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2);
        var path = W(c.A) + "/notifications";
        await AssignNotification(host, owner, c, "Existing notification");
        await Body(await host.MemberWriteAsync(recipient, HttpMethod.Put, path + "/preferences", new { inAppEnabled = true, taskAssignedEnabled = false, expectedVersion = "0" }));
        await AssignNotification(host, owner, c, "Muted event"); Assert.Equal(1, (await Body(await recipient.GetAsync(path))).GetProperty("totalCount").GetInt32());
        await Body(await host.MemberWriteAsync(recipient, HttpMethod.Put, path + "/preferences", new { inAppEnabled = false, taskAssignedEnabled = true, expectedVersion = "1" }));
        await AssignNotification(host, owner, c, "Muted channel"); Assert.Equal(1, (await Body(await recipient.GetAsync(path))).GetProperty("totalCount").GetInt32());
        await Body(await host.MemberWriteAsync(recipient, HttpMethod.Put, path + "/preferences", new { inAppEnabled = true, taskAssignedEnabled = true, expectedVersion = "2" }));
        await AssignNotification(host, owner, c, "New notification"); var page = await Body(await recipient.GetAsync(path));
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32()); Assert.DoesNotContain("Muted", page.ToString());
        await using var db = fixture.CreateContext(); Assert.Equal(4, await db.OutboxEvents.CountAsync(e => e.WorkspaceId == c.A.Id && e.Kind == "Project.TaskAssigned"));
    }

    [MySqlFact]
    public async Task NotificationBulkReadIsAtomicAndDoesNotIncludeNewArrivals()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2);
        await AssignNotification(host, owner, c, "First"); await AssignNotification(host, owner, c, "Second"); var path = W(c.A) + "/notifications";
        var page = await Body(await recipient.GetAsync(path)); var ids = page.GetProperty("items").EnumerateArray().Select(n => n.GetProperty("id").GetString()!).ToArray();
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/read", new { notificationIds = new[] { ids[0], long.MaxValue.ToString() } }), HttpStatusCode.NotFound);
        Assert.Equal(2, (await Body(await recipient.GetAsync(path + "/unread-count"))).GetProperty("unreadCount").GetInt32());
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/read", new { notificationIds = ids }), HttpStatusCode.NotFound);
        await AssignNotification(host, owner, c, "Arrived after selection");
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/read", new { notificationIds = ids })).StatusCode);
        Assert.Equal(1, (await Body(await recipient.GetAsync(path + "/unread-count"))).GetProperty("unreadCount").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/read", new { notificationIds = ids })).StatusCode);
        var unread = await Body(await recipient.GetAsync(path + "?unreadOnly=true")); Assert.Equal("Arrived after selection", unread.GetProperty("items")[0].GetProperty("taskTitle").GetString());
        await using var db = fixture.CreateContext(); var readTimes = await db.UserNotifications.Where(n => n.WorkspaceId == c.A.Id && n.ReadAtUtc != null).Select(n => n.ReadAtUtc).ToArrayAsync(); Assert.Equal(2, readTimes.Length); Assert.Single(readTimes.Distinct());
    }

    [MySqlFact]
    public async Task NotificationPreferenceAndBulkInputsCannotSelectOtherAccountsOrBypassCsrf()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2); using var outsider = await host.LoginAsync(1);
        await AssignNotification(host, owner, c); var path = W(c.A) + "/notifications"; var id = (await Body(await recipient.GetAsync(path))).GetProperty("items")[0].GetProperty("id").GetString()!;
        foreach (var ids in new[] { Array.Empty<string>(), new[] { id, id }, new[] { id, "0" + id }, new[] { "bad" }, Enumerable.Repeat(id, 101).ToArray() })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/read", new { notificationIds = ids })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.PostAsJsonAsync(path + "/read", new { notificationIds = new[] { id } })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/read", new { notificationIds = new[] { id }, userId = host.Users[0].Id.ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Put, path + "/preferences", new { inAppEnabled = false, taskAssignedEnabled = true, expectedVersion = "0", userId = host.Users[0].Id.ToString() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Put, path + "/preferences", new { inAppEnabled = false, expectedVersion = "0" })).StatusCode);
        await Problem(await outsider.GetAsync(path + "/preferences?userId=1"), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(outsider, HttpMethod.Put, path + "/preferences", new { inAppEnabled = true }), HttpStatusCode.NotFound);
        await using (var db = fixture.CreateContext()) await db.WorkspaceMemberships.Where(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[2].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.State, WorkspaceMembershipState.Suspended));
        await Problem(await recipient.GetAsync(path + "/preferences"), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/read", new { notificationIds = new[] { id } }), HttpStatusCode.NotFound);
    }
}
