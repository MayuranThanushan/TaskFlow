using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact]
    public async Task VersionedBusinessAuditCapturesSafeDeltasAndRequestCorrelationAcrossBothSaves()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var path = W(c.A) + "/projects/" + c.A1.Id;
        var changed = await host.MemberWriteAsync(owner, HttpMethod.Put, path, new { name = "PRIVATE_AUDIT_TITLE_CANARY", description = "PRIVATE_AUDIT_BODY_CANARY" });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var rows = (await Body(await owner.GetAsync(path + "/activity"))).GetProperty("items").EnumerateArray().ToArray();
        var audit = rows.Single(row => row.GetProperty("actionType").GetString() == "ProjectUpdated").GetProperty("audit");
        Assert.Equal(1, audit.GetProperty("schemaVersion").GetInt32()); Assert.False(audit.GetProperty("isLegacy").GetBoolean());
        Assert.Equal("Succeeded", audit.GetProperty("outcome").GetString()); Assert.Equal(changed.Headers.GetValues("X-Trace-Id").Single(), audit.GetProperty("correlationId").GetString());
        Assert.Equal("Project", audit.GetProperty("subjectType").GetString()); Assert.Equal(c.A1.Id.ToString(), audit.GetProperty("subjectId").GetString());
        Assert.All(audit.GetProperty("changes").EnumerateArray(), delta => Assert.True(delta.GetProperty("redacted").GetBoolean()));
        Assert.DoesNotContain("PRIVATE_AUDIT", audit.ToString());
        var task = W(c.A) + "/tasks/" + c.TaskA.Id;
        await Body(await host.MemberWriteAsync(owner, HttpMethod.Patch, task, new { priority = "High", assigneeId = (string?)null }));
        var events = (await Body(await owner.GetAsync(path + "/activity"))).GetProperty("items").EnumerateArray().ToArray();
        var edited = events.Single(e => e.GetProperty("actionType").GetString() == "TaskUpdated").GetProperty("audit");
        Assert.Contains(edited.GetProperty("changes").EnumerateArray(), d => d.GetProperty("field").GetString() == "priority" && d.GetProperty("before").GetString() == "Medium" && d.GetProperty("after").GetString() == "High");
        var unassigned = events.Single(e => e.GetProperty("actionType").GetString() == "TaskUnassigned").GetProperty("audit");
        Assert.Single(unassigned.GetProperty("changes").EnumerateArray());
        Assert.Equal("assigneeId", unassigned.GetProperty("changes")[0].GetProperty("field").GetString());
        await using var db = fixture.CreateContext();
        Assert.DoesNotContain("PRIVATE_AUDIT", string.Join("\n", await db.ActivityLogs.Where(a => a.ProjectId == c.A1.Id).Select(a => a.ChangesJson).ToArrayAsync()));
    }

    [MySqlFact]
    public async Task LegacyRowsRemainExplicitlyUnknownAndHaveNoInventedDeltas()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO `ActivityLogs` (`WorkspaceId`,`ProjectId`,`ActorUserId`,`ActionType`,`CreatedAtUtc`) VALUES ({c.A.Id},{c.A1.Id},{host.Users[0].Id},'ProjectUpdated',UTC_TIMESTAMP(6))");
        var rows = (await Body(await owner.GetAsync(W(c.A) + "/projects/" + c.A1.Id + "/activity"))).GetProperty("items").EnumerateArray();
        var old = rows.Single(r => r.GetProperty("actionType").GetString() == "ProjectUpdated").GetProperty("audit");
        Assert.True(old.GetProperty("isLegacy").GetBoolean()); Assert.Equal(0, old.GetProperty("schemaVersion").GetInt32()); Assert.Equal("LegacyUnknown", old.GetProperty("outcome").GetString());
        Assert.Empty(old.GetProperty("changes").EnumerateArray()); Assert.Equal(JsonValueKind.Null, old.GetProperty("correlationId").ValueKind);
    }

    [MySqlFact]
    public async Task SuccessfulPasswordProofSurvivesGovernanceRollbackWithoutASuccessfulBusinessAudit()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var result = await host.MemberWriteAsync(owner, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[0].Id + "/role", new { role = "Member", password = GovernancePassword });
        await Problem(result, HttpStatusCode.Conflict);
        await using var db = fixture.CreateContext();
        var proof = await db.SecurityEvents.SingleAsync(e => e.SubjectUserId == host.Users[0].Id && e.Action == "PasswordProof");
        Assert.Equal("Succeeded", proof.Outcome); Assert.Equal(result.Headers.GetValues("X-Trace-Id").Single(), proof.CorrelationId);
        Assert.False(await db.WorkspaceAuditLogs.AnyAsync(a => a.WorkspaceId == c.A.Id && a.Action == "CompanyRoleChanged"));
        Assert.Equal(WorkspaceRole.Owner, (await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[0].Id)).Role);
        var serialized = JsonSerializer.Serialize(proof); Assert.DoesNotContain(GovernancePassword, serialized); Assert.DoesNotContain(host.Users[0].Email!, serialized);
    }

    [MySqlFact]
    public async Task CoalescedRejectedSignalsAreBoundedAndNeverReadableByCompanyAdmins()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        for (var i = 0; i < 3; i++)
            await Problem(await owner.PostAsync(W(c.A) + "/leave", System.Net.Http.Json.JsonContent.Create(new { password = "MUST_NOT_APPEAR" })), HttpStatusCode.BadRequest);
        await using var db = fixture.CreateContext();
        var rejected = await db.SecurityEvents.Where(e => e.SubjectUserId == host.Users[0].Id && e.Action == "RequestRejected" && e.StatusCode == 400).ToArrayAsync();
        Assert.Single(rejected); Assert.True(rejected[0].Coalesced); Assert.Contains("{workspaceId}", rejected[0].RouteTemplate);
        Assert.DoesNotContain("MUST_NOT_APPEAR", JsonSerializer.Serialize(rejected));
        foreach (var path in new[] { "/api/v1/security-events", W(c.A) + "/security-events", W(c.A) + "/audit/security" }) await Problem(await owner.GetAsync(path), HttpStatusCode.NotFound);
        var audit = await Body(await owner.GetAsync(W(c.A) + "/audit")); Assert.DoesNotContain("RequestRejected", audit.ToString()); Assert.DoesNotContain("PasswordProof", audit.ToString());
        var entry = rejected[0]; entry.Outcome = "Tampered"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [MySqlFact]
    public async Task AuditSinkFailureRollsBackBusinessRegistrationAndLogoutAndNeverIssuesALoginSession()
    {
        var fail = new FailAuditStore(); await using var host = new ProjectHost(fixture, true, true, true, fail); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var fresh = host.Client();
        fail.Security = true;
        await Problem(await host.MemberWriteAsync(fresh, HttpMethod.Post, "/api/v1/auth/login", new { email = host.Users[0].Email, password = GovernancePassword }), HttpStatusCode.InternalServerError);
        Assert.Equal(HttpStatusCode.Unauthorized, (await fresh.GetAsync("/api/v1/auth/me")).StatusCode);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v1/auth/logout"), HttpStatusCode.InternalServerError);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/auth/me")).StatusCode);
        var email = "audit-rollback-" + Guid.NewGuid().ToString("N") + "@example.test";
        await Problem(await host.MemberWriteAsync(fresh, HttpMethod.Post, "/api/v1/auth/register", new { email, displayName = "No orphan", password = GovernancePassword }), HttpStatusCode.InternalServerError);
        await using (var db = fixture.CreateContext()) Assert.False(await db.Users.AnyAsync(u => u.Email == email));
        fail.Security = false; fail.Business = true;
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects", new { name = "MUST_ROLL_BACK_PROJECT" }), HttpStatusCode.InternalServerError);
        await using var check = fixture.CreateContext(); Assert.False(await check.Projects.AnyAsync(p => p.Name == "MUST_ROLL_BACK_PROJECT"));
    }
    private sealed class FailAuditStore : SaveChangesInterceptor
    {
        public bool Security { get; set; }
        public bool Business { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (data.Context!.ChangeTracker.Entries().Any(e => e.State == EntityState.Added && (Security && e.Entity is SecurityEvent || Business && e.Entity is IVersionedAudit))) throw new InvalidOperationException("Synthetic audit-store failure.");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}
