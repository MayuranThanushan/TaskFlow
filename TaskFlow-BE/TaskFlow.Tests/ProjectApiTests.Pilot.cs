using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
using TaskFlow.Infrastructure.Identity;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    private static object Policy(string version, bool exports = true, bool hold = true, int audit = 365, int content = 30, string password = GovernancePassword) =>
        new { expectedVersion = version, auditRetentionDays = audit, deletedContentRetentionDays = content, legalHold = hold, auditExportsEnabled = exports, password };
    [MySqlFact]
    public async Task PilotProjectAdmissionSerializesCompetingRequestsAndDeletionOnlyReleasesActiveCapacity()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var one = await host.LoginAsync(0); using var two = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext()) { db.Projects.AddRange(Enumerable.Range(0, 17).Select(i => new Project { WorkspaceId = c.A.Id, OwnerId = host.Users[0].Id, Name = "Capacity " + i })); await db.SaveChangesAsync(); }
        var path = W(c.A) + "/projects";
        var results = await Task.WhenAll(host.MemberWriteAsync(one, HttpMethod.Post, path, new { name = "Winner one" }), host.MemberWriteAsync(two, HttpMethod.Post, path, new { name = "Winner two" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created); var denied = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("quota.exceeded", (await Problem(denied, HttpStatusCode.Conflict)).GetProperty("code").GetString());
        await using (var db = fixture.CreateContext()) { Assert.Equal(20, await db.Projects.CountAsync(p => p.WorkspaceId == c.A.Id)); Assert.Equal(1, await db.ActivityLogs.CountAsync(a => a.WorkspaceId == c.A.Id && a.ActionType == ActivityAction.ProjectCreated)); }
        await host.MemberWriteAsync(one, HttpMethod.Delete, W(c.A) + "/projects/" + c.A2.Id);
        Assert.Equal(HttpStatusCode.Created, (await host.MemberWriteAsync(one, HttpMethod.Post, path, new { name = "After deletion" })).StatusCode);
        var capacity = (await Body(await one.GetAsync(W(c.A) + "/policy"))).GetProperty("capacity"); Assert.Equal(20, capacity.GetProperty("activeProjects").GetInt32()); Assert.Equal(21, capacity.GetProperty("retainedProjects").GetInt32());
    }
    [MySqlFact]
    public async Task PilotTaskAdmissionSerializesAtProjectAndCompanyBounds()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var one = await host.LoginAsync(0); using var two = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext()) { db.Tasks.AddRange(Enumerable.Range(0, 498).Select(_ => new TaskItem { WorkspaceId = c.A.Id, ProjectId = c.A1.Id, Title = "Capacity", Status = TaskState.Done })); await db.SaveChangesAsync(); }
        var path = W(c.A) + "/projects/" + c.A1.Id + "/tasks";
        var results = await Task.WhenAll(host.MemberWriteAsync(one, HttpMethod.Post, path, new { title = "Task one" }), host.MemberWriteAsync(two, HttpMethod.Post, path, new { title = "Task two" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        await using (var db = fixture.CreateContext())
        {
            var projects = Enumerable.Range(0, 3).Select(i => new Project { WorkspaceId = c.A.Id, OwnerId = host.Users[0].Id, Name = "Task bucket " + i }).ToArray(); db.Projects.AddRange(projects); await db.SaveChangesAsync();
            // A1 has 500; A2 has 1. Fill the company to 1,999 without exceeding any project's 500 bound.
            db.Tasks.AddRange(Enumerable.Range(0, 1498).Select(i => new TaskItem { WorkspaceId = c.A.Id, ProjectId = projects[i / 500].Id, Title = "Fleet capacity" })); await db.SaveChangesAsync();
        }
        var other = W(c.A) + "/projects/" + c.A2.Id + "/tasks";
        var last = await Task.WhenAll(host.MemberWriteAsync(one, HttpMethod.Post, other, new { title = "Last one" }), host.MemberWriteAsync(two, HttpMethod.Post, other, new { title = "Last two" }));
        Assert.Single(last, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(last, r => r.StatusCode == HttpStatusCode.Conflict);
        await using var verify = fixture.CreateContext(); Assert.Equal(2000, await verify.Tasks.CountAsync(t => t.WorkspaceId == c.A.Id)); Assert.Equal(2, await verify.Tasks.CountAsync(t => t.WorkspaceId == c.B.Id));
    }
    [MySqlFact]
    public async Task RetainedCapacityBlocksCreateDeleteLoopsWithoutBlockingExistingReads()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext())
        {
            db.Projects.AddRange(Enumerable.Range(0, 98).Select(_ => new Project { WorkspaceId = c.A.Id, OwnerId = host.Users[0].Id, Name = "Retained", DeletedAtUtc = host.Clock.Now.UtcDateTime }));
            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Tasks (WorkspaceId,ProjectId,Title,Status,Priority,CreatedAtUtc,UpdatedAtUtc,DeletedAtUtc,Version) SELECT {c.A.Id},{c.A1.Id},'Retained','ToDo','Medium',UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),UTC_TIMESTAMP(6),1 FROM (SELECT 0 UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) a CROSS JOIN (SELECT 0 UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) b CROSS JOIN (SELECT 0 UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) d CROSS JOIN (SELECT 0 UNION ALL SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3 UNION ALL SELECT 4 UNION ALL SELECT 5 UNION ALL SELECT 6 UNION ALL SELECT 7 UNION ALL SELECT 8 UNION ALL SELECT 9) e LIMIT 9998");
        }
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "Over retained cap" }), HttpStatusCode.Conflict);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/tasks", new { title = "Over retained cap" }), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(W(c.A) + "/projects/" + c.A1.Id)).StatusCode);
        await using var dbCheck = fixture.CreateContext(); Assert.False(await dbCheck.ActivityLogs.AnyAsync(a => a.WorkspaceId == c.A.Id && a.ActionType == ActivityAction.ProjectCreated));
    }
    [MySqlFact]
    public async Task PolicyChangesRequireOwnerProofAndRevisionAndInvalidateExistingExportTickets()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2);
        var path = W(c.A) + "/policy"; var initial = await Body(await owner.GetAsync(path)); Assert.False(initial.GetProperty("automaticPurgeEnabled").GetBoolean()); Assert.True(initial.GetProperty("legalHold").GetBoolean());
        var version = initial.GetProperty("version").GetString()!;
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Put, path, Policy(version, false)), HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Put, path, Policy(version, false, password: "Incorrect-proof")), HttpStatusCode.Forbidden);
        var ticket = (await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/audit/console/exports", new { filters = new { } }))).GetProperty("ticket").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Put, path, Policy(version, false, false, 180, 90))).StatusCode);
        var saved = await Body(await owner.GetAsync(path)); Assert.Equal("2", saved.GetProperty("version").GetString()); Assert.False(saved.GetProperty("auditExportsEnabled").GetBoolean()); Assert.False(saved.GetProperty("legalHold").GetBoolean());
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/audit/console/exports/download", new { ticket }), HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/audit/console/exports", new { filters = new { } }), HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Put, path, Policy(version)), HttpStatusCode.Conflict);
        await Problem(await owner.GetAsync(path + "?raw=true"), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Put, path, new { expectedVersion = "2", auditRetentionDays = 180, deletedContentRetentionDays = 90, password = GovernancePassword }), HttpStatusCode.BadRequest);
        await using var db = fixture.CreateContext(); var audit = await db.WorkspaceAuditLogs.SingleAsync(a => a.WorkspaceId == c.A.Id && a.Action == "CompanyPolicyChanged");
        Assert.Equal("Workspace", audit.SubjectType); Assert.Contains("auditExportsEnabled", audit.ChangesJson); Assert.DoesNotContain(GovernancePassword, audit.ChangesJson);
        Assert.True(await db.Tasks.AnyAsync(t => t.Id == c.TaskA.Id));
    }
    [MySqlFact]
    public async Task PilotCompanyAdmissionCannotOversubscribeItsLastSlot()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var one = await host.LoginAsync(0); using var two = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext())
        {
            var count = await db.Workspaces.CountAsync(); Assert.InRange(count, 0, 9);
            var fill = Enumerable.Range(0, 9 - count).Select(i => new Workspace { Name = "Gate fixture " + i }).ToArray(); db.Workspaces.AddRange(fill); await db.SaveChangesAsync();
            db.WorkspaceMemberships.AddRange(fill.Select(w => new WorkspaceMembership { WorkspaceId = w.Id, UserId = host.Users[1].Id, Role = WorkspaceRole.Owner })); await db.SaveChangesAsync();
        }
        var results = await Task.WhenAll(host.MemberWriteAsync(one, HttpMethod.Post, "/api/v2/workspaces", new { name = "Last company one" }), host.MemberWriteAsync(two, HttpMethod.Post, "/api/v2/workspaces", new { name = "Last company two" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        await using var verify = fixture.CreateContext(); Assert.Equal(10, await verify.Workspaces.CountAsync());
    }
    [MySqlFact]
    public async Task PilotSeatReactivationAtCapacityIsDeniedAndDoesNotRestoreAccess()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext())
        {
            var target = await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[2].Id); target.State = WorkspaceMembershipState.Suspended;
            // Fixture-only identities are also owned by the host cleanup; no credentials or outbound registration.
            var added = Enumerable.Range(0, 24).Select(i => new ApplicationUser { DisplayName = "Seat fixture", UserName = "seat-" + Guid.NewGuid().ToString("N") + "@example.test", Email = "seat-" + Guid.NewGuid().ToString("N") + "@example.test", NormalizedEmail = Guid.NewGuid().ToString("N"), SecurityStamp = Guid.NewGuid().ToString("N") }).ToArray();
            db.Users.AddRange(added); await db.SaveChangesAsync(); host.Users.AddRange(added);
            db.WorkspaceMemberships.AddRange(added.Select((u, index) => new WorkspaceMembership { WorkspaceId = c.A.Id, UserId = u.Id, Role = WorkspaceRole.Member, State = index == 23 ? WorkspaceMembershipState.Suspended : WorkspaceMembershipState.Active })); await db.SaveChangesAsync();
        }
        var path = W(c.A) + "/members/" + host.Users[2].Id + "/state";
        using var second = await host.LoginAsync(0);
        var results = await Task.WhenAll(host.MemberWriteAsync(owner, HttpMethod.Patch, path, new { state = "Active" }),
            host.MemberWriteAsync(second, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[^1].Id + "/state", new { state = "Active" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        await using var verify = fixture.CreateContext(); Assert.Equal(25, await verify.WorkspaceMemberships.CountAsync(m => m.WorkspaceId == c.A.Id && m.State == WorkspaceMembershipState.Active));
        Assert.Equal(1, await verify.WorkspaceAuditLogs.CountAsync(a => a.WorkspaceId == c.A.Id && a.Action == "CompanyMemberReactivated"));
    }

    [MySqlFact]
    public async Task TenantRegressionMatrixHasNoSupportOverrideAndPreservesOtherCompanyAccessAfterSuspension()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var otherOwner = await host.LoginAsync(1);
        var email = "support-" + Guid.NewGuid().ToString("N") + "@example.test"; using var registration = host.Client();
        var user = await Body(await host.MemberWriteAsync(registration, HttpMethod.Post, "/api/v1/auth/register", new { email, displayName = "Support without grant", password = GovernancePassword }));
        var supportId = long.Parse(user.GetProperty("id").GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        host.Users.Add(new ApplicationUser { Id = supportId, Email = email });
        await using (var db = fixture.CreateContext())
        {
            db.UserClaims.Add(new Microsoft.AspNetCore.Identity.IdentityUserClaim<long> { UserId = supportId, ClaimType = System.Security.Claims.ClaimTypes.Role, ClaimValue = "Support" });
            db.WorkspaceMemberships.Add(new WorkspaceMembership { WorkspaceId = c.B.Id, UserId = host.Users[2].Id, Role = WorkspaceRole.Member });
            await db.SaveChangesAsync(); // Persist the manually constrained company parent before its project grant.
            db.ProjectMembers.Add(new ProjectMember { WorkspaceId = c.B.Id, ProjectId = c.B1.Id, UserId = host.Users[2].Id, Role = MembershipRole.Editor });
            (await db.ProjectMembers.SingleAsync(m => m.ProjectId == c.B1.Id && m.UserId == host.Users[0].Id)).Role = MembershipRole.Viewer;
            await db.SaveChangesAsync();
        }
        try
        {
        using var support = await host.LoginAsync(3); using var shared = await host.LoginAsync(2);
        foreach (var suffix in new[] { "", "/projects", "/projects/" + c.A1.Id, "/tasks/" + c.TaskA.Id, "/projects/" + c.A1.Id + "/tasks", "/projects/" + c.A1.Id + "/members", "/projects/" + c.A1.Id + "/activity", "/activity", "/dashboard", "/members", "/audit", "/audit/console", "/policy" })
        {
            await Problem(await support.GetAsync(W(c.A) + suffix), HttpStatusCode.NotFound);
            await Problem(await otherOwner.GetAsync(W(c.A) + suffix), HttpStatusCode.NotFound);
        }
        Assert.Equal(0, (await Body(await support.GetAsync("/api/v2/workspaces"))).GetProperty("totalCount").GetInt32());
        await Problem(await host.MemberWriteAsync(support, HttpMethod.Put, W(c.A) + "/policy", Policy("1")), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(support, HttpMethod.Post, W(c.A) + "/audit/console/exports", new { filters = new { } }), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Body(await shared.GetAsync(W(c.A) + "/projects"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await shared.GetAsync(W(c.A) + "/audit/console?source=Projects"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(W(c.B) + "/projects/" + c.B1.Id)).StatusCode);
        await Problem(await RevisionWrite(owner, HttpMethod.Patch, W(c.B) + "/tasks/" + c.TaskB.Id, "invalid", new { title = "Viewer denied" }), HttpStatusCode.Forbidden);
        await Problem(await owner.GetAsync(W(c.B) + "/projects/" + c.HiddenB.Id), HttpStatusCode.NotFound);
        await Problem(await RevisionWrite(owner, HttpMethod.Patch, W(c.A) + "/tasks/" + c.TaskB.Id, "invalid", new { title = "Foreign denied" }), HttpStatusCode.NotFound);
        await host.MemberWriteAsync(owner, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[2].Id + "/state", new { state = "Suspended" });
        await Problem(await shared.GetAsync(W(c.A) + "/policy"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await shared.GetAsync(W(c.B) + "/projects/" + c.B1.Id)).StatusCode);
        }
        finally
        {
            // Keep Support present throughout every request, including security-stamp claim refresh.
            await using var cleanup = fixture.CreateContext(); await cleanup.UserClaims.Where(x => x.UserId == supportId).ExecuteDeleteAsync();
        }
        await using var verify = fixture.CreateContext(); Assert.False(await verify.Tasks.AnyAsync(t => t.Title == "Viewer denied" || t.Title == "Foreign denied"));
        Assert.Equal(2, await verify.Projects.CountAsync(p => p.WorkspaceId == c.A.Id)); Assert.Equal(2, await verify.Projects.CountAsync(p => p.WorkspaceId == c.B.Id));
    }

    [MySqlFact]
    public async Task PolicyAuditFailureRollsBackRevisionAndControlsWithoutLosingProofFact()
    {
        var failure = new FailGovernanceAudit(); await using var host = new ProjectHost(fixture, true, true, true, failure); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        failure.Enabled = true;
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Put, W(c.A) + "/policy", Policy("1", false, false, 180, 90)), HttpStatusCode.InternalServerError);
        await using var db = fixture.CreateContext(); var company = await db.Workspaces.SingleAsync(w => w.Id == c.A.Id);
        Assert.Equal(1, company.PolicyVersion); Assert.True(company.LegalHold); Assert.True(company.AuditExportsEnabled); Assert.Equal(365, company.AuditRetentionDays);
        Assert.False(await db.WorkspaceAuditLogs.AnyAsync(a => a.WorkspaceId == c.A.Id && a.Action == "CompanyPolicyChanged"));
        Assert.True(await db.SecurityEvents.AnyAsync(e => e.SubjectUserId == host.Users[0].Id && e.Action == "PasswordProof" && e.Outcome == "Succeeded"));
    }

    [MySqlFact]
    public async Task PilotCompanyCreationAlsoBoundsCurrentOwnerAdmission()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var one = await host.LoginAsync(0); using var two = await host.LoginAsync(0);
        Assert.Equal(HttpStatusCode.Created, (await host.MemberWriteAsync(one, HttpMethod.Post, "/api/v2/workspaces", new { name = "Second owned company" })).StatusCode);
        var results = await Task.WhenAll(host.MemberWriteAsync(one, HttpMethod.Post, "/api/v2/workspaces", new { name = "Third one" }), host.MemberWriteAsync(two, HttpMethod.Post, "/api/v2/workspaces", new { name = "Third two" }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        await using var db = fixture.CreateContext(); Assert.Equal(3, await db.WorkspaceMemberships.CountAsync(m => m.UserId == host.Users[0].Id && m.State == WorkspaceMembershipState.Active && m.Role == WorkspaceRole.Owner));
    }

}
