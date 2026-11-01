using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact]
    public async Task AuditConsoleFiltersIntersectAndDeletedProjectsRemainPrivate()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2);
        var path = W(c.A) + "/audit/console";
        var updated = await host.MemberWriteAsync(owner, HttpMethod.Put, W(c.A) + "/projects/" + c.A1.Id, new { name = "PRIVATE_CONSOLE_CANARY" });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Delete, W(c.A) + "/projects/" + c.A1.Id)).StatusCode);
        var rows = await Body(await owner.GetAsync(path + "?source=Projects&resourceState=Deleted&action=ProjectDeleted&entityType=Project&entityId=" + c.A1.Id + "&actorId=" + host.Users[0].Id + "&outcome=Succeeded"));
        Assert.Single(rows.GetProperty("items").EnumerateArray()); Assert.True(rows.GetProperty("items")[0].GetProperty("projectDeleted").GetBoolean());
        Assert.DoesNotContain("PRIVATE_CONSOLE_CANARY", rows.ToString());
        Assert.Equal(0, (await Body(await admin.GetAsync(path + "?source=Projects&resourceState=Deleted"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "?source=Projects&entityId=" + c.B1.Id))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "?source=Projects&resourceState=Active&action=ProjectDeleted"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "?source=Projects&fromUtc=2099-01-01T00:00:00Z"))).GetProperty("totalCount").GetInt32());
        await Problem(await owner.GetAsync(W(c.B) + "/audit/console"), HttpStatusCode.Forbidden);
    }
    [MySqlFact]
    public async Task AuditConsoleGovernanceIsRedactedAndFiltersLegacyWithoutInventingOutcomes()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await host.MemberWriteAsync(owner, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[2].Id + "/role", new { role = "Member" });
        var path = W(c.A) + "/audit/console";
        var result = await Body(await owner.GetAsync(path + "?action=CompanyRoleChanged&entityType=WorkspaceMembership&outcome=Succeeded"));
        Assert.Single(result.GetProperty("items").EnumerateArray()); Assert.Equal("Governance", result.GetProperty("items")[0].GetProperty("source").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, result.GetProperty("items")[0].GetProperty("projectDeleted").ValueKind);
        await using (var db = fixture.CreateContext()) await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WorkspaceAuditLogs (WorkspaceId,ActorUserId,TargetUserId,Action,CreatedAtUtc) VALUES ({c.A.Id},{host.Users[0].Id},{host.Users[2].Id},'CompanyRoleChanged',UTC_TIMESTAMP(6))");
        var old = await Body(await owner.GetAsync(path + "?outcome=LegacyUnknown"));
        Assert.Single(old.GetProperty("items").EnumerateArray()); Assert.True(old.GetProperty("items")[0].GetProperty("audit").GetProperty("isLegacy").GetBoolean());
        Assert.DoesNotContain("PasswordProof", (await Body(await owner.GetAsync(path))).ToString());
    }
    [MySqlFact]
    public async Task AuditExportRechecksCurrentGrantsAndBindsActorCompanyAndExpiry()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2);
        await using (var db = fixture.CreateContext()) { db.ProjectMembers.Add(new ProjectMember { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, UserId = host.Users[2].Id, Role = MembershipRole.Viewer }); await db.SaveChangesAsync(); }
        var path = W(c.A) + "/audit/console";
        var ticket = (await Body(await host.MemberWriteAsync(admin, HttpMethod.Post, path + "/exports", new { filters = new { source = "Projects" } }))).GetProperty("ticket").GetString();
        await using (var db = fixture.CreateContext()) { (await db.ProjectMembers.SingleAsync(m => m.ProjectId == c.A1.Id && m.UserId == host.Users[2].Id)).RevokedAtUtc = host.Clock.Now.UtcDateTime; await db.SaveChangesAsync(); }
        var result = await Body(await host.MemberWriteAsync(admin, HttpMethod.Post, path + "/exports/download", new { ticket })); Assert.Empty(result.GetProperty("items").EnumerateArray());
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/exports/download", new { ticket }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Post, path + "/exports/download", new { ticket = ticket + "x" }), HttpStatusCode.NotFound);
        host.Clock.Now = host.Clock.Now.AddMinutes(6);
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Post, path + "/exports/download", new { ticket }), HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task AuditExportCannotSurviveSuspensionAndCannotCrossCompanies()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2);
        var path = W(c.A) + "/audit/console";
        var ticket = (await Body(await host.MemberWriteAsync(admin, HttpMethod.Post, path + "/exports", new { filters = new { } }))).GetProperty("ticket").GetString();
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.B) + "/audit/console/exports/download", new { ticket }), HttpStatusCode.NotFound);
        await host.MemberWriteAsync(owner, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[2].Id + "/state", new { state = "Suspended" });
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Post, path + "/exports/download", new { ticket }), HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task AuditExportIsBoundedAndExcludesEventsCreatedAfterItsTicket()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext())
        {
            db.ActivityLogs.AddRange(Enumerable.Range(0, 1001).Select(_ => new ActivityLog { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.ProjectUpdated }));
            await db.SaveChangesAsync();
        }
        var path = W(c.A) + "/audit/console";
        var ticket = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/exports", new { filters = new { source = "Projects", action = "ProjectUpdated" } }))).GetProperty("ticket").GetString();
        var changed = await host.MemberWriteAsync(owner, HttpMethod.Put, W(c.A) + "/projects/" + c.A1.Id, new { name = "AFTER_TICKET_CANARY" });
        var trace = changed.Headers.GetValues("X-Trace-Id").Single();
        var result = await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/exports/download", new { ticket }));
        Assert.Equal(1000, result.GetProperty("items").GetArrayLength()); Assert.True(result.GetProperty("truncated").GetBoolean()); Assert.DoesNotContain(trace, result.ToString());
        Assert.All(result.GetProperty("items").EnumerateArray(), r => Assert.Equal(System.Text.Json.JsonValueKind.String, r.GetProperty("id").ValueKind));
    }
    [MySqlFact]
    public async Task AuditConsoleRejectsInvalidUnboundedAndRepeatedFiltersAndHonorsRollout()
    {
        await using (var disabled = new ProjectHost(fixture, true, true)) { await disabled.InitializeAsync(); var c = await SeedCompanies(disabled); using var user = await disabled.LoginAsync(0); await Problem(await user.GetAsync(W(c.A) + "/audit/console"), HttpStatusCode.NotFound); }
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var company = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var path = W(company.A) + "/audit/console";
        foreach (var query in new[] { "actorId=0", "actorId=9223372036854775808", "page=502", "pageSize=101", "page=501&pageSize=100", "source=Security", "source=Governance&resourceState=Deleted", "outcome=Denied", "entityType=Secret", "fromUtc=2026-11-01", "fromUtc=2027-01-01T00:00:00Z&toUtc=2026-01-01T00:00:00Z", "page=1&page=2", "raw=true" }) await Problem(await owner.GetAsync(path + "?" + query), HttpStatusCode.BadRequest);
        await Problem(await owner.PostAsJsonAsync(path + "/exports", new { filters = new { } }), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/exports", new { filters = new { }, actorId = "1" }), HttpStatusCode.BadRequest);
    }
    [MySqlFact]
    public async Task AuditConsoleTimestampBoundsRespectOffsetsAndMicrosecondPrecision()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var stamp = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234560);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO ActivityLogs (WorkspaceId,ProjectId,ActorUserId,ActionType,CreatedAtUtc) VALUES ({c.A.Id},{c.A1.Id},{host.Users[0].Id},'ProjectUpdated',{stamp})");
        var path = W(c.A) + "/audit/console?source=Projects&action=ProjectUpdated";
        Assert.Equal(1, (await Body(await owner.GetAsync(path + "&fromUtc=2026-11-01T00:00:00.123456Z&toUtc=2026-11-01T00:00:00.123456Z"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "&fromUtc=2026-11-01T00:00:00.1234561Z"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "&toUtc=2026-11-01T00:00:00.1234559Z"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await Body(await owner.GetAsync(path + "&fromUtc=2026-11-01T05:30:00.123456%2B05:30"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await owner.GetAsync(path + "&fromUtc=9999-12-31T23:59:59.9999999Z"))).GetProperty("totalCount").GetInt32());
    }

}
