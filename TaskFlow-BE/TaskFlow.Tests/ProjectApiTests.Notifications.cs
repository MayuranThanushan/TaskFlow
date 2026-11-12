using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Notifications;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    private async Task GrantNotificationRecipient(ProjectHost host, Companies c)
    {
        await using var db = fixture.CreateContext();
        db.ProjectMembers.Add(new() { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, UserId = host.Users[2].Id, Role = MembershipRole.Editor }); await db.SaveChangesAsync();
    }
    private Task<HttpResponseMessage> AssignNotification(ProjectHost host, HttpClient owner, Companies c, string title = "Private notification canary") =>
        host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + $"/projects/{c.A1.Id}/tasks", new { title, assigneeId = host.Users[2].Id.ToString() });

    [MySqlFact]
    public async Task NotificationsCommitWithAssignmentAndHaveSelfOnlyReadUnreadState()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c);
        using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2); using var otherSession = await host.LoginAsync(2);
        Assert.Equal(HttpStatusCode.Created, (await AssignNotification(host, owner, c)).StatusCode);
        var path = W(c.A) + "/notifications"; var page = await Body(await recipient.GetAsync(path));
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32()); Assert.Equal(1, page.GetProperty("unreadCount").GetInt32());
        var item = page.GetProperty("items")[0]; var id = item.GetProperty("id").GetString();
        Assert.Equal("TaskAssigned", item.GetProperty("kind").GetString()); Assert.Equal("Private notification canary", item.GetProperty("taskTitle").GetString());
        Assert.DoesNotContain("email", item.ToString(), StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("eventId", item.ToString());
        Assert.Equal(0, (await Body(await owner.GetAsync(path))).GetProperty("totalCount").GetInt32());
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, path + "/" + id, new { isRead = true }), HttpStatusCode.NotFound);
        var writes = await Task.WhenAll(host.MemberWriteAsync(recipient, HttpMethod.Patch, path + "/" + id, new { isRead = true }), host.MemberWriteAsync(otherSession, HttpMethod.Patch, path + "/" + id, new { isRead = true }));
        Assert.All(writes, r => Assert.Equal(HttpStatusCode.NoContent, r.StatusCode));
        Assert.Equal(0, (await Body(await recipient.GetAsync(path + "/unread-count"))).GetProperty("unreadCount").GetInt32());
        Assert.Empty((await Body(await recipient.GetAsync(path + "?unreadOnly=true"))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(recipient, HttpMethod.Patch, path + "/" + id, new { isRead = false })).StatusCode);
        Assert.Equal(1, (await Body(await recipient.GetAsync(path + "/unread-count"))).GetProperty("unreadCount").GetInt32());
        await using var db = fixture.CreateContext(); var row = await db.UserNotifications.SingleAsync(n => n.WorkspaceId == c.A.Id);
        Assert.Equal(host.Users[2].Id, row.RecipientUserId); Assert.Null(row.ReadAtUtc);
        Assert.Equal("Project.TaskAssigned", (await db.OutboxEvents.SingleAsync(e => e.Id == row.EventId)).Kind);
    }

    [MySqlFact]
    public async Task NotificationsHideRevokedProjectsDeletedTasksAndRemovedCompanies()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c);
        using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2);
        await AssignNotification(host, owner, c); var path = W(c.A) + "/notifications";
        var id = (await Body(await recipient.GetAsync(path))).GetProperty("items")[0].GetProperty("id").GetString();
        await using (var db = fixture.CreateContext()) await db.ProjectMembers.IgnoreQueryFilters().Where(m => m.ProjectId == c.A1.Id && m.UserId == host.Users[2].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.RevokedAtUtc, DateTime.UtcNow));
        var page = await Body(await recipient.GetAsync(path)); Assert.Equal(0, page.GetProperty("totalCount").GetInt32()); Assert.Equal(0, page.GetProperty("unreadCount").GetInt32()); Assert.DoesNotContain("canary", page.ToString());
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Patch, path + "/" + id, new { isRead = true }), HttpStatusCode.NotFound);
        await using (var db = fixture.CreateContext())
        {
            await db.ProjectMembers.IgnoreQueryFilters().Where(m => m.ProjectId == c.A1.Id && m.UserId == host.Users[2].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.RevokedAtUtc, (DateTime?)null));
            await db.Tasks.IgnoreQueryFilters().Where(t => t.ProjectId == c.A1.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedAtUtc, DateTime.UtcNow));
        }
        Assert.Equal(0, (await Body(await recipient.GetAsync(path + "/unread-count"))).GetProperty("unreadCount").GetInt32());
        await using (var db = fixture.CreateContext()) await db.WorkspaceMemberships.Where(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[2].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.State, WorkspaceMembershipState.Removed));
        var denied = await recipient.GetAsync(path + "?page=invalid"); await Problem(denied, HttpStatusCode.NotFound); Assert.DoesNotContain("canary", await denied.Content.ReadAsStringAsync());
    }

    [MySqlFact]
    public async Task NotificationsSuppressSelfActionsAndDefaultOffDoesNotBackfillHistory()
    {
        await using (var host = new ProjectHost(fixture, true, true, true))
        {
            await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0);
            Assert.Equal(HttpStatusCode.Created, (await AssignNotification(host, owner, c)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync(W(c.A) + "/notifications")).StatusCode);
            await using var db = fixture.CreateContext(); Assert.False(await db.UserNotifications.AnyAsync(n => n.WorkspaceId == c.A.Id));
        }
        await using (var host = new ProjectHost(fixture, true, true, true, false, true))
        {
            await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
            Assert.Equal(HttpStatusCode.Created, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + $"/projects/{c.A1.Id}/tasks", new { title = "Self", assigneeId = host.Users[0].Id.ToString() })).StatusCode);
            await using var db = fixture.CreateContext(); Assert.False(await db.UserNotifications.AnyAsync(n => n.WorkspaceId == c.A.Id));
        }
    }

    [MySqlFact]
    public async Task NotificationsPaginationValidationScopeAndCsrfAreBounded()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c);
        using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2); using var outsider = await host.LoginAsync(1); using var anonymous = host.Client();
        await AssignNotification(host, owner, c, "First"); await AssignNotification(host, owner, c, "Second"); var path = W(c.A) + "/notifications";
        var first = await Body(await recipient.GetAsync(path + "?pageSize=1")); Assert.Equal(2, first.GetProperty("totalCount").GetInt32()); Assert.Equal(2, first.GetProperty("unreadCount").GetInt32()); Assert.Equal("Second", first.GetProperty("items")[0].GetProperty("taskTitle").GetString());
        Assert.Equal("First", (await Body(await recipient.GetAsync(path + "?page=2&pageSize=1"))).GetProperty("items")[0].GetProperty("taskTitle").GetString());
        foreach (var query in new[] { "page=0", "pageSize=101", "page=501&pageSize=100", "unreadOnly=bad", "page=1&page=2", "recipientUserId=1" }) Assert.Equal(HttpStatusCode.BadRequest, (await recipient.GetAsync(path + "?" + query)).StatusCode);
        await Problem(await outsider.GetAsync(path + "?page=bad"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        var id = first.GetProperty("items")[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await recipient.PatchAsJsonAsync(path + "/" + id, new { isRead = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Patch, path + "/" + id, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Patch, path + "/" + id, new { isRead = true, recipientUserId = "1" })).StatusCode);
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Patch, W(c.B) + "/notifications/" + id, new { isRead = true }), HttpStatusCode.NotFound);
    }

    private sealed class FailNotification : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        {
            if (data.Context!.ChangeTracker.Entries<UserNotification>().Any(e => e.State == EntityState.Added)) throw new InvalidOperationException("Synthetic notification failure");
            return ValueTask.FromResult(result);
        }
    }
    [MySqlFact]
    public async Task NotificationFailureRollsBackBusinessAuditAndOutboxTogether()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true, new FailNotification());
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0);
        Assert.Equal(HttpStatusCode.InternalServerError, (await AssignNotification(host, owner, c)).StatusCode);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Tasks.AnyAsync(t => t.Title == "Private notification canary"));
        Assert.False(await db.UserNotifications.AnyAsync(n => n.WorkspaceId == c.A.Id));
        Assert.False(await db.OutboxEvents.AnyAsync(e => e.WorkspaceId == c.A.Id));
        Assert.False(await db.ActivityLogs.AnyAsync(a => a.WorkspaceId == c.A.Id && a.ActionType == ActivityAction.TaskAssigned));
    }

    [MySqlFact]
    public async Task NotificationUniquenessScopeAndReplayCannotRetargetHistoricalAssignments()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0);
        await AssignNotification(host, owner, c);
        await using (var db = fixture.CreateContext())
        {
            var row = await db.UserNotifications.SingleAsync(n => n.WorkspaceId == c.A.Id);
            db.UserNotifications.Add(new() { WorkspaceId = row.WorkspaceId, RecipientUserId = row.RecipientUserId, EventId = row.EventId, ProjectId = row.ProjectId, TaskId = row.TaskId, CreatedAtUtc = row.CreatedAtUtc });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>(); await using var transaction = await db.Database.BeginTransactionAsync();
            var row = await db.UserNotifications.SingleAsync(n => n.WorkspaceId == c.A.Id); var source = await db.OutboxEvents.SingleAsync(e => e.Id == row.EventId);
            db.WorkspaceMemberships.Add(new() { WorkspaceId = c.A.Id, UserId = host.Users[1].Id, Role = WorkspaceRole.Member });
            await db.SaveChangesAsync();
            db.ProjectMembers.Add(new() { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, UserId = host.Users[1].Id, Role = MembershipRole.Editor });
            await db.SaveChangesAsync();
            await db.Tasks.IgnoreQueryFilters().Where(t => t.Id == row.TaskId).ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeId, host.Users[1].Id));
            await NotificationMaterializer.CaptureAsync(db, source); await db.SaveChangesAsync(); await transaction.CommitAsync();
            Assert.Equal(1, await db.UserNotifications.CountAsync(n => n.WorkspaceId == c.A.Id));
        }
        await using (var db = fixture.CreateContext())
        {
            var row = await db.UserNotifications.SingleAsync(n => n.WorkspaceId == c.A.Id); row.RecipientUserId = host.Users[0].Id;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
    }

    [MySqlFact]
    public async Task NotificationParentAndRecipientCompanyReferencesAreDatabaseProtected()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0);
        await AssignNotification(host, owner, c);
        await using (var db = fixture.CreateContext())
        {
            var row = await db.UserNotifications.SingleAsync(n => n.WorkspaceId == c.A.Id);
            db.UserNotifications.Add(new() { WorkspaceId = c.B.Id, RecipientUserId = host.Users[1].Id, EventId = row.EventId, ProjectId = c.B1.Id, TaskId = c.TaskB.Id, CreatedAtUtc = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = fixture.CreateContext())
        {
            var row = await db.UserNotifications.SingleAsync(n => n.WorkspaceId == c.A.Id);
            db.UserNotifications.Add(new() { WorkspaceId = c.A.Id, RecipientUserId = host.Users[0].Id, EventId = row.EventId, ProjectId = c.B1.Id, TaskId = c.TaskB.Id, CreatedAtUtc = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }

    [MySqlFact]
    public async Task NotificationTitlesAreCurrentAuthorizedDataAndNeverStoredSnapshots()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2);
        await AssignNotification(host, owner, c);
        await using (var db = fixture.CreateContext())
        {
            var row = await db.UserNotifications.SingleAsync(n => n.WorkspaceId == c.A.Id);
            Assert.DoesNotContain("canary", System.Text.Json.JsonSerializer.Serialize(new { row.Id, row.WorkspaceId, row.TaskId, row.Kind }));
            var task = await db.Tasks.SingleAsync(t => t.Id == row.TaskId); task.Title = "Current task title";
            var project = await db.Projects.SingleAsync(p => p.Id == c.A1.Id); project.Name = "Current project title"; await db.SaveChangesAsync();
        }
        var item = (await Body(await recipient.GetAsync(W(c.A) + "/notifications"))).GetProperty("items")[0];
        Assert.Equal("Current task title", item.GetProperty("taskTitle").GetString()); Assert.Equal("Current project title", item.GetProperty("projectName").GetString());
        Assert.DoesNotContain("canary", item.ToString());
    }
}
