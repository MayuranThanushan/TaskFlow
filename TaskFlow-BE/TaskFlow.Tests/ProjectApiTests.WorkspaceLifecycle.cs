using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    private const string GovernancePassword = "Project-Tests-Password123!";

    [MySqlFact]
    public async Task LifecycleRequiresFlagSessionCsrfAndClosesAllLegacyResourceRoutes()
    {
        await using (var off = new ProjectHost(fixture, true, true))
        {
            await off.InitializeAsync(); var c = await SeedCompanies(off); using var actor = await off.LoginAsync(0);
            await Problem(await actor.GetAsync(W(c.A) + "/members"), HttpStatusCode.NotFound);
        }
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var companies = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var anonymous = host.Client();
        await Problem(await anonymous.GetAsync(W(companies.A) + "/members"), HttpStatusCode.Unauthorized);
        await Problem(await owner.PostAsync(W(companies.A) + "/leave", System.Net.Http.Json.JsonContent.Create(new { })), HttpStatusCode.BadRequest);
        foreach (var path in new[] { "/api/v1/projects", "/api/v1/projects/" + companies.A1.Id, "/api/v1/tasks/" + companies.TaskA.Id, "/api/v1/dashboard", "/api/v1/projects/" + companies.A1.Id + "/members" })
            await Problem(await owner.GetAsync(path), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, "/api/v1/tasks/" + companies.TaskA.Id + "/status", new { status = "Done" }), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/auth/me")).StatusCode);
        await Problem(await owner.GetAsync(W(companies.A) + "/members?scope=other"), HttpStatusCode.BadRequest);
        await Problem(await owner.GetAsync(W(companies.A) + "/members?page=2147483647"), HttpStatusCode.BadRequest);
        await Problem(await owner.GetAsync(W(companies.A) + "/audit?page=2147483647"), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, W(companies.A) + "/members/" + host.Users[2].Id + "/role", new { role = "Owner", actorId = "1" }), HttpStatusCode.BadRequest);
    }

    [MySqlFact]
    public async Task ViewerCanReadButCannotMutateOrBeAssignedAndDowngradeClearsAssignments()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(1); using var viewer = await host.LoginAsync(0);
        var freshGrant = await Body(await host.MemberWriteAsync(viewer, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/members", new { userId = host.Users[2].Id.ToString(), role = "Viewer" }));
        Assert.Equal("Viewer", freshGrant.GetProperty("role").GetString());
        var project = W(c.B) + "/projects/" + c.B1.Id; var user = host.Users[0].Id;
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Put, project + "/members/" + user, new { role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(project)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(project + "/tasks")).StatusCode);
        await Problem(await host.MemberWriteAsync(viewer, HttpMethod.Post, project + "/tasks", new { title = "Denied" }), HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(viewer, HttpMethod.Patch, W(c.B) + "/tasks/" + c.TaskB.Id + "/status", new { status = "Done" }), HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, W(c.B) + "/tasks/" + c.TaskB.Id, new { assigneeId = user.ToString() }), HttpStatusCode.BadRequest);
        await using (var db = fixture.CreateContext()) { Assert.Null((await db.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).AssigneeId); Assert.Equal(host.Users[2].Id, (await db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == c.DeletedB.Id)).AssigneeId); }
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Put, project + "/members/" + user, new { role = "Editor" })).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.MemberWriteAsync(viewer, HttpMethod.Post, project + "/tasks", new { title = "Editor can create" })).StatusCode);
        await Problem(await host.MemberWriteAsync(viewer, HttpMethod.Delete, W(c.B) + "/tasks/" + c.TaskB.Id), HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Put, project + "/members/" + user, new { role = "Owner" }), HttpStatusCode.BadRequest);
    }

    [MySqlFact]
    public async Task SuspensionClearsActiveAssignmentsRetainsHistoryAndReactivationDoesNotRestoreAssignments()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(1); using var member = await host.LoginAsync(0);
        await using (var db = fixture.CreateContext()) { var history = await db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == c.DeletedB.Id); history.AssigneeId = host.Users[0].Id; await db.SaveChangesAsync(); }
        var endpoint = W(c.B) + "/members/" + host.Users[0].Id + "/state";
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Patch, endpoint, new { state = "Suspended" })).StatusCode);
        await Problem(await member.GetAsync(W(c.B) + "/projects/" + c.B1.Id), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(member, HttpMethod.Post, W(c.B) + "/projects/" + c.B1.Id + "/tasks", new { title = "After suspension" }), HttpStatusCode.NotFound);
        await using (var db = fixture.CreateContext()) { Assert.Null((await db.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).AssigneeId); Assert.Equal(host.Users[0].Id, (await db.Tasks.IgnoreQueryFilters().SingleAsync(t => t.Id == c.DeletedB.Id)).AssigneeId); Assert.NotNull(await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == c.B1.Id && m.UserId == host.Users[0].Id && m.RevokedAtUtc == null)); }
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Patch, endpoint, new { state = "Active" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(W(c.B) + "/projects/" + c.B1.Id)).StatusCode);
        await using var check = fixture.CreateContext(); Assert.Null((await check.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).AssigneeId);
    }

    [MySqlFact]
    public async Task CompanyRolesAndDirectoryAuditNeverGrantPrivateProjectAccessOrExposeContent()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2); using var guest = await host.LoginAsync(0); using var ownerB = await host.LoginAsync(1);
        await Problem(await admin.GetAsync(W(c.A) + "/projects/" + c.A1.Id), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/ownership", new { userId = host.Users[2].Id.ToString(), password = GovernancePassword }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[0].Id + "/role", new { role = "Member" }), HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[2].Id + "/role", new { role = "Owner", password = GovernancePassword }), HttpStatusCode.Forbidden);
        await Problem(await guest.GetAsync(W(c.B) + "/members"), HttpStatusCode.Forbidden);
        await Problem(await guest.GetAsync(W(c.B) + "/audit"), HttpStatusCode.Forbidden);
        var directory = await Body(await owner.GetAsync(W(c.A) + "/members")); Assert.DoesNotContain("email", directory.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(ownerB, HttpMethod.Put, W(c.B) + "/projects/" + c.B1.Id + "/members/" + host.Users[0].Id, new { role = "Viewer" })).StatusCode);
        var audit = await Body(await ownerB.GetAsync(W(c.B) + "/audit")); Assert.Contains("ProjectRoleChanged", audit.ToString()); Assert.DoesNotContain(c.B1.Name, audit.ToString()); Assert.DoesNotContain(c.HiddenB.Name, audit.ToString());
        await Problem(await admin.GetAsync(W(c.B) + "/audit"), HttpStatusCode.NotFound);
    }

    [MySqlFact]
    public async Task OwnershipNeedsPasswordAndLastOwnerAndOwnedProjectDepartureAreProtected()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2);
        var rolePath = W(c.A) + "/members/" + host.Users[0].Id + "/role";
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Patch, rolePath, new { role = "Member", password = GovernancePassword }), HttpStatusCode.Conflict);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/leave", new { password = GovernancePassword }), HttpStatusCode.Conflict);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/ownership", new { userId = host.Users[2].Id.ToString(), password = "Wrong-password!" }), HttpStatusCode.Forbidden);
        await using (var failedPassword = fixture.CreateContext()) Assert.Equal(1, (await failedPassword.Users.SingleAsync(u => u.Id == host.Users[0].Id)).AccessFailedCount);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/ownership", new { userId = host.Users[2].Id.ToString(), password = GovernancePassword })).StatusCode);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/leave", new { }), HttpStatusCode.Conflict);
        foreach (var project in new[] { c.A1, c.A2 })
            Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/projects/" + project.Id + "/ownership", new { userId = host.Users[2].Id.ToString(), password = GovernancePassword })).StatusCode);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Delete, W(c.A) + "/projects/" + c.A1.Id), HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/leave", new { })).StatusCode);
        await Problem(await owner.GetAsync(W(c.A) + "/dashboard"), HttpStatusCode.NotFound);
        await using var db = fixture.CreateContext(); Assert.Equal(WorkspaceMembershipState.Removed, (await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[0].Id)).State);
        Assert.All(await db.ProjectMembers.Where(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[0].Id).ToArrayAsync(), m => Assert.NotNull(m.RevokedAtUtc));
        Assert.Equal(1, await db.WorkspaceMemberships.CountAsync(m => m.WorkspaceId == c.A.Id && m.Role == WorkspaceRole.Owner && m.State == WorkspaceMembershipState.Active));
    }

    [MySqlFact]
    public async Task SuspensionAndAssignmentSerializeInBothOrdersAndAuditFailureRollsBackCleanup()
    {
        foreach (var suspensionFirst in new[] { true, false })
        {
            var gate = new ParentLockGate(); await using var host = new ProjectHost(fixture, true, true, true, gate); await host.InitializeAsync(); var c = await SeedCompanies(host);
            using var owner = await host.LoginAsync(1); using var writer = await host.LoginAsync(1);
            Task<HttpResponseMessage> Suspend() => host.MemberWriteAsync(owner, HttpMethod.Patch, W(c.B) + "/members/" + host.Users[0].Id + "/state", new { state = "Suspended" });
            Task<HttpResponseMessage> Assign() => host.MemberWriteAsync(writer, HttpMethod.Patch, W(c.B) + "/tasks/" + c.TaskB.Id, new { assigneeId = host.Users[0].Id.ToString() });
            var result = suspensionFirst ? await gate.OrderedAsync(Suspend, Assign) : await gate.OrderedAsync(Assign, Suspend);
            Assert.Equal(suspensionFirst ? HttpStatusCode.NoContent : HttpStatusCode.OK, result.First.StatusCode);
            Assert.Equal(suspensionFirst ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent, result.Second.StatusCode);
            await using var db = fixture.CreateContext(); Assert.Null((await db.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).AssigneeId);
        }
        var fail = new FailGovernanceAudit();
        await using var rollback = new ProjectHost(fixture, true, true, true, fail); await rollback.InitializeAsync(); var company = await SeedCompanies(rollback); using var actor = await rollback.LoginAsync(1);
        fail.Enabled = true;
        await Problem(await rollback.MemberWriteAsync(actor, HttpMethod.Patch, W(company.B) + "/members/" + rollback.Users[0].Id + "/state", new { state = "Suspended" }), HttpStatusCode.InternalServerError);
        await using var check = fixture.CreateContext(); Assert.Equal(WorkspaceMembershipState.Active, (await check.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == company.B.Id && m.UserId == rollback.Users[0].Id)).State); Assert.Equal(rollback.Users[0].Id, (await check.Tasks.SingleAsync(t => t.Id == company.TaskB.Id)).AssigneeId);
    }
    [MySqlFact]
    public async Task CompetingOwnerDemotionsKeepAnActiveOwnerAndViewerDowngradeCannotRestoreAnAssignment()
    {
        var gate = new ParentLockGate();
        await using (var host = new ProjectHost(fixture, true, true, true, gate))
        {
            await host.InitializeAsync(); var c = await SeedCompanies(host);
            await using (var db = fixture.CreateContext()) { var membership = await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[2].Id); membership.Role = WorkspaceRole.Owner; await db.SaveChangesAsync(); }
            using var first = await host.LoginAsync(0); using var second = await host.LoginAsync(2);
            var result = await gate.OrderedAsync(() => host.MemberWriteAsync(first, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[0].Id + "/role", new { role = "Member", password = GovernancePassword }),
                () => host.MemberWriteAsync(second, HttpMethod.Patch, W(c.A) + "/members/" + host.Users[2].Id + "/role", new { role = "Member", password = GovernancePassword }));
            Assert.Equal(HttpStatusCode.NoContent, result.First.StatusCode); Assert.Equal(HttpStatusCode.Conflict, result.Second.StatusCode);
            await using var check = fixture.CreateContext(); Assert.Equal(1, await check.WorkspaceMemberships.CountAsync(m => m.WorkspaceId == c.A.Id && m.Role == WorkspaceRole.Owner && m.State == WorkspaceMembershipState.Active));
        }
        foreach (var downgradeFirst in new[] { true, false })
        {
            var ordered = new ParentLockGate(); await using var host = new ProjectHost(fixture, true, true, true, ordered); await host.InitializeAsync(); var c = await SeedCompanies(host);
            using var owner = await host.LoginAsync(1); using var writer = await host.LoginAsync(1);
            Task<HttpResponseMessage> Downgrade() => host.MemberWriteAsync(owner, HttpMethod.Put, W(c.B) + "/projects/" + c.B1.Id + "/members/" + host.Users[0].Id, new { role = "Viewer" });
            Task<HttpResponseMessage> Assign() => host.MemberWriteAsync(writer, HttpMethod.Patch, W(c.B) + "/tasks/" + c.TaskB.Id, new { assigneeId = host.Users[0].Id.ToString() });
            var result = downgradeFirst ? await ordered.OrderedAsync(Downgrade, Assign) : await ordered.OrderedAsync(Assign, Downgrade);
            Assert.Equal(downgradeFirst ? HttpStatusCode.NoContent : HttpStatusCode.OK, result.First.StatusCode);
            Assert.Equal(downgradeFirst ? HttpStatusCode.BadRequest : HttpStatusCode.NoContent, result.Second.StatusCode);
            await using var db = fixture.CreateContext(); Assert.Null((await db.Tasks.SingleAsync(t => t.Id == c.TaskB.Id)).AssigneeId);
        }
    }

    [MySqlFact]
    public async Task GovernanceAuditIsAppendOnlyScopedAndNoOpRoleChangesDoNotCreateEvents()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var endpoint = W(c.A) + "/members/" + host.Users[2].Id + "/role";
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Patch, endpoint, new { role = "Member" })).StatusCode);
        await using (var db = fixture.CreateContext())
        {
            var audit = await db.WorkspaceAuditLogs.SingleAsync(a => a.WorkspaceId == c.A.Id);
            audit.Action = "Tampered"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Patch, endpoint, new { role = "Member" })).StatusCode);
        await using (var db = fixture.CreateContext()) Assert.Equal(1, await db.WorkspaceAuditLogs.CountAsync(a => a.WorkspaceId == c.A.Id));
        using var member = await host.LoginAsync(2);
        var directory = await Body(await member.GetAsync(W(c.A) + "/members"));
        Assert.All(directory.GetProperty("items").EnumerateArray(), row => { Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("role").ValueKind); Assert.Equal(System.Text.Json.JsonValueKind.Null, row.GetProperty("state").ValueKind); });
        await using (var db = fixture.CreateContext())
        {
            db.WorkspaceAuditLogs.Add(new WorkspaceAuditLog { WorkspaceId = c.A.Id, ActorUserId = host.Users[0].Id, TargetUserId = host.Users[1].Id, Action = "InvalidScope" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        await using (var db = fixture.CreateContext())
        {
            db.WorkspaceAuditLogs.Add(new WorkspaceAuditLog { WorkspaceId = c.A.Id, ActorUserId = host.Users[0].Id, TargetUserId = host.Users[2].Id, ProjectId = c.B1.Id, Action = "InvalidProject" });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
    [MySqlFact]
    public async Task SensitiveGovernanceRequestsAreBoundedWithoutAutomaticReplay()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0);
        var endpoint = W(c.A) + "/members/" + host.Users[2].Id + "/role";
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Patch, endpoint, new { role = "Admin" })).StatusCode);
        var rejected = await host.MemberWriteAsync(owner, HttpMethod.Patch, endpoint, new { role = "Admin" });
        await Problem(rejected, HttpStatusCode.TooManyRequests); Assert.NotNull(rejected.Headers.RetryAfter);
        await using var db = fixture.CreateContext(); Assert.Equal(0, await db.WorkspaceAuditLogs.CountAsync(a => a.WorkspaceId == c.A.Id));
    }
    private sealed class FailGovernanceAudit : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<WorkspaceAuditLog>().Any(e => e.State == EntityState.Added)) throw new InvalidOperationException("Synthetic audit failure.");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}
