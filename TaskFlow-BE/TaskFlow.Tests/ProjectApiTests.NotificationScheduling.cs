using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Notifications;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private static IConfiguration ScheduleConfiguration(bool enabled = true) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Notifications:Enabled"] = "true", ["Notifications:SchedulingEnabled"] = enabled.ToString(),
        ["WorkspaceReads:Enabled"] = "true", ["WorkspaceWrites:Enabled"] = "true", ["WorkspaceLifecycle:Enabled"] = "true"
    }).Build();
    private async Task<NotificationBatchResult> Schedule(long workspace, DateTimeOffset now)
    {
        var clock = new FixedClock { Now = now }; await using var db = fixture.CreateContext(clock);
        return await new NotificationScheduler(db, clock, ScheduleConfiguration()).RunAsync(workspace);
    }
    private async Task<TaskItem> ScheduledTask(ProjectHost host, Companies c, DateTime due)
    {
        await using var db = fixture.CreateContext();
        var task = new TaskItem { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, AssigneeId = host.Users[2].Id, Title = "Scheduled work", DueDateUtc = due };
        db.Tasks.Add(task); await db.SaveChangesAsync(); return task;
    }
    private async Task EnableSchedule(ProjectHost host, Companies c, bool reminders = true, bool digest = true)
    {
        await using var db = fixture.CreateContext();
        db.NotificationPreferences.Add(new() { WorkspaceId = c.A.Id, UserId = host.Users[2].Id, DueReminderEnabled = reminders, DailyDigestEnabled = digest, TimeZoneId = "Asia/Colombo", DigestHour = 9 }); await db.SaveChangesAsync();
    }
    [MySqlFact]
    public async Task ScheduledDeliveryIsIdempotentAcrossConcurrentBatchesAndHonorsCompanyScope()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true); await host.InitializeAsync();
        var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); await EnableSchedule(host, c);
        var now = new DateTimeOffset(2026, 11, 13, 3, 45, 0, TimeSpan.Zero); await ScheduledTask(host, c, now.UtcDateTime.AddMinutes(-10));
        await Task.WhenAll(Schedule(c.A.Id, now), Schedule(c.A.Id, now)); await Schedule(c.A.Id, now);
        await Schedule(c.B.Id, now);
        using var recipient = await host.LoginAsync(2); using var owner = await host.LoginAsync(0);
        var page = await Body(await recipient.GetAsync(W(c.A) + "/notifications")); Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.All(page.GetProperty("items").EnumerateArray(), item => Assert.Equal("InAppDelivered", item.GetProperty("deliveryStatus").GetString()));
        Assert.Equal(0, (await Body(await owner.GetAsync(W(c.A) + "/notifications"))).GetProperty("totalCount").GetInt32());
        await using var db = fixture.CreateContext(); var rows = await db.UserNotifications.Where(n => n.WorkspaceId == c.A.Id).ToArrayAsync();
        Assert.Equal(2, rows.Length); Assert.All(rows, n => { Assert.Null(n.EventId); Assert.NotNull(n.ScheduleKey); });
        Assert.Empty(await db.OutboxEvents.Where(e => e.WorkspaceId == c.A.Id && e.Kind.Contains("Digest")).ToArrayAsync());
    }
    [MySqlFact]
    public async Task ScheduledRowsDisappearAfterCompletionRescheduleReassignmentOrRevocation()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true); await host.InitializeAsync();
        var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); await EnableSchedule(host, c, true, false);
        var now = new DateTimeOffset(2026, 11, 13, 3, 45, 0, TimeSpan.Zero); var task = await ScheduledTask(host, c, now.UtcDateTime);
        await Schedule(c.A.Id, now); using var recipient = await host.LoginAsync(2); var path = W(c.A) + "/notifications";
        var id = (await Body(await recipient.GetAsync(path))).GetProperty("items")[0].GetProperty("id").GetString();
        await using (var db = fixture.CreateContext()) await db.Tasks.IgnoreQueryFilters().Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.DueDateUtc, now.UtcDateTime.AddHours(1)));
        Assert.Equal(0, (await Body(await recipient.GetAsync(path))).GetProperty("totalCount").GetInt32());
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Patch, path + "/" + id, new { isRead = true }), HttpStatusCode.NotFound);
        await Schedule(c.A.Id, now.AddHours(1)); Assert.Equal(1, (await Body(await recipient.GetAsync(path))).GetProperty("totalCount").GetInt32());
        await using (var db = fixture.CreateContext()) await db.Tasks.IgnoreQueryFilters().Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskState.Done));
        Assert.Equal(0, (await Body(await recipient.GetAsync(path + "/unread-count"))).GetProperty("unreadCount").GetInt32());
        await using (var db = fixture.CreateContext()) await db.Tasks.IgnoreQueryFilters().Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, TaskState.ToDo).SetProperty(t => t.AssigneeId, host.Users[0].Id));
        await Schedule(c.A.Id, now.AddHours(1)); Assert.Equal(0, (await Body(await recipient.GetAsync(path))).GetProperty("totalCount").GetInt32());
        await using (var db = fixture.CreateContext()) await db.Tasks.IgnoreQueryFilters().Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeId, host.Users[2].Id));
        await using (var db = fixture.CreateContext()) await db.ProjectMembers.IgnoreQueryFilters().Where(m => m.ProjectId == c.A1.Id && m.UserId == host.Users[2].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.RevokedAtUtc, now.UtcDateTime));
        await Schedule(c.A.Id, now.AddHours(1)); Assert.Equal(0, (await Body(await recipient.GetAsync(path))).GetProperty("totalCount").GetInt32());
    }
    [MySqlFact]
    public async Task SchedulingDefaultsOffAndSuppressesMutedDeletedCompletedAndOldTasks()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c);
        var now = new DateTimeOffset(2026, 11, 13, 3, 45, 0, TimeSpan.Zero);
        var task = await ScheduledTask(host, c, now.UtcDateTime); Assert.Equal(0, (await Schedule(c.A.Id, now)).Delivered);
        await EnableSchedule(host, c, true, false);
        await using (var db = fixture.CreateContext()) await db.Tasks.IgnoreQueryFilters().Where(t => t.Id == task.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.DeletedAtUtc, now.UtcDateTime));
        Assert.Equal(0, (await Schedule(c.A.Id, now)).Delivered);
        await ScheduledTask(host, c, now.UtcDateTime.AddDays(-2)); Assert.Equal(0, (await Schedule(c.A.Id, now)).Delivered);
        await using (var db = fixture.CreateContext()) await db.NotificationPreferences.Where(p => p.WorkspaceId == c.A.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.InAppEnabled, false));
        await ScheduledTask(host, c, now.UtcDateTime); Assert.Equal(0, (await Schedule(c.A.Id, now)).Delivered);
        await using var disabled = fixture.CreateContext(); await Assert.ThrowsAsync<InvalidOperationException>(() => new NotificationScheduler(disabled, TimeProvider.System, ScheduleConfiguration(false)).RunAsync(c.A.Id));
    }
    [MySqlFact]
    public async Task DigestRespectsLocalHourAndOneOccurrencePerLocalDay()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); await EnableSchedule(host, c, false, true);
        var now = new DateTimeOffset(2026, 11, 13, 3, 30, 0, TimeSpan.Zero); await ScheduledTask(host, c, now.UtcDateTime.AddHours(2));
        Assert.Equal(0, (await Schedule(c.A.Id, now.AddMinutes(-1))).Delivered); Assert.Equal(1, (await Schedule(c.A.Id, now)).Delivered);
        Assert.Equal(0, (await Schedule(c.A.Id, now.AddMinutes(20))).Delivered); Assert.Equal(0, (await Schedule(c.A.Id, now.AddHours(1))).Delivered);
        Assert.Equal(1, (await Schedule(c.A.Id, now.AddDays(1))).Delivered);
    }
    [MySqlFact]
    public async Task SchedulePreferencesValidateAndOldClientsPreserveSchedule()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var recipient = await host.LoginAsync(2); var path = W(c.A) + "/notifications/preferences";
        foreach (var zone in new[] { "Invalid/Zone", "Eastern Standard Time", "" })
            Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Put, path, new { inAppEnabled = true, taskAssignedEnabled = true, expectedVersion = "0", dueReminderEnabled = true, dailyDigestEnabled = true, timeZoneId = zone, digestHour = 9 })).StatusCode);
        var saved = await Body(await host.MemberWriteAsync(recipient, HttpMethod.Put, path, new { inAppEnabled = true, taskAssignedEnabled = true, expectedVersion = "0", dueReminderEnabled = true, dailyDigestEnabled = true, timeZoneId = "Asia/Colombo", digestHour = 9 })); Assert.True(saved.GetProperty("dailyDigestEnabled").GetBoolean());
        var preserved = await Body(await host.MemberWriteAsync(recipient, HttpMethod.Put, path, new { inAppEnabled = true, taskAssignedEnabled = false, expectedVersion = "1" })); Assert.True(preserved.GetProperty("dueReminderEnabled").GetBoolean()); Assert.Equal("Asia/Colombo", preserved.GetProperty("timeZoneId").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await host.MemberWriteAsync(recipient, HttpMethod.Put, path, new { inAppEnabled = true, taskAssignedEnabled = true, expectedVersion = "2", dueReminderEnabled = true })).StatusCode);
    }
    [MySqlFact]
    public async Task SchedulerRollbackLeavesNeitherCursorNorPartialDeliveryAndRetrySucceeds()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); await EnableSchedule(host, c);
        var now = new DateTimeOffset(2026, 11, 13, 3, 45, 0, TimeSpan.Zero); await ScheduledTask(host, c, now.UtcDateTime);
        var clock = new FixedClock { Now = now };
        var options = new DbContextOptionsBuilder<TaskFlow.Infrastructure.Persistence.TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(new FailScheduledCommit()).Options;
        await using (var db = new TaskFlow.Infrastructure.Persistence.TaskFlowDbContext(options, clock))
            await Assert.ThrowsAsync<InvalidOperationException>(() => new NotificationScheduler(db, clock, ScheduleConfiguration()).RunAsync(c.A.Id));
        await using (var db = fixture.CreateContext()) { Assert.False(await db.UserNotifications.AnyAsync(n => n.WorkspaceId == c.A.Id)); Assert.False(await db.NotificationScheduleCursors.AnyAsync(n => n.WorkspaceId == c.A.Id)); }
        Assert.Equal(2, (await Schedule(c.A.Id, now)).Delivered); Assert.Equal(0, (await Schedule(c.A.Id, now)).Delivered);
    }
    private sealed class FailScheduledCommit : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic post-save failure");
    }
    [MySqlFact]
    public async Task SchedulerCursorAdvancesPastIneligibleTasksAndWrapsWithoutDuplicates()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host, c); await EnableSchedule(host, c, true, false);
        var now = new DateTimeOffset(2026, 11, 13, 3, 45, 0, TimeSpan.Zero);
        await using (var db = fixture.CreateContext()) { for (var i = 0; i < 101; i++) db.Tasks.Add(new() { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, Title = "Ineligible prefix" }); await db.SaveChangesAsync(); }
        await ScheduledTask(host, c, now.UtcDateTime);
        var first = await Schedule(c.A.Id, now); Assert.Equal(100, first.Scanned); Assert.Equal(0, first.Delivered); Assert.True(first.NextTaskId > 0);
        var second = await Schedule(c.A.Id, now); Assert.Equal(1, second.Delivered); Assert.Equal(0, second.NextTaskId);
        await Schedule(c.A.Id, now); Assert.Equal(0, (await Schedule(c.A.Id, now)).Delivered);
    }

}
