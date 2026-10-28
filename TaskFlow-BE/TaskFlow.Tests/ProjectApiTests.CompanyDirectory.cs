using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact]
    public async Task CompanyCreationRequiresFlagsSessionCsrfAndRejectsInjectedOwnership()
    {
        await using (var disabled = new ProjectHost(fixture, true))
        {
            await disabled.InitializeAsync(); using var user = await disabled.LoginAsync(0);
            await Problem(await disabled.MemberWriteAsync(user, HttpMethod.Post, "/api/v2/workspaces", new { name = "Off" }), HttpStatusCode.NotFound);
        }
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync();
        using var owner = await host.LoginAsync(0); using var anonymous = host.Client();
        await Problem(await host.MemberWriteAsync(anonymous, HttpMethod.Post, "/api/v2/workspaces", new { name = "Anonymous" }), HttpStatusCode.Unauthorized);
        await Problem(await owner.PostAsJsonAsync("/api/v2/workspaces", new { name = "No token" }), HttpStatusCode.BadRequest);
        foreach (var body in new object[] { new { name = " " }, new { name = new string('x', 151) }, new { name = "Injected", ownerId = host.Users[1].Id.ToString() }, new { name = "Injected", role = "Admin" }, new { name = "Injected", legacyOwnerId = "1" } })
            await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v2/workspaces", body), HttpStatusCode.BadRequest);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v2/workspaces?workspaceId=1", new { name = "Query" }), HttpStatusCode.BadRequest);
    }

    [MySqlFact]
    public async Task CreatingTwoCompaniesKeepsOwnerMembershipsResourcesAndDeepLinksSeparate()
    {
        await using var host = new ProjectHost(fixture, true, true); await host.InitializeAsync();
        using var owner = await host.LoginAsync(0); using var other = await host.LoginAsync(1);
        var created = await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v2/workspaces", new { name = "  Alpha  " });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var a = await Body(created); var id = a.GetProperty("id").GetString()!; var path = "/api/v2/workspaces/" + id;
        Assert.Equal("Alpha", a.GetProperty("name").GetString()); Assert.Equal("Owner", a.GetProperty("role").GetString());
        Assert.Equal(path, created.Headers.Location!.OriginalString);
        var b = await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v2/workspaces", new { name = "Beta" }));
        var bPath = "/api/v2/workspaces/" + b.GetProperty("id").GetString();
        Assert.Equal("Alpha", (await Body(await owner.GetAsync(path))).GetProperty("name").GetString());
        await Problem(await other.GetAsync(path), HttpStatusCode.NotFound);
        await Problem(await owner.GetAsync(path + "?page=1"), HttpStatusCode.BadRequest);
        var project = await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/projects", new { name = "Private Alpha" }));
        await Problem(await owner.GetAsync(bPath + "/projects/" + project.GetProperty("id").GetString()), HttpStatusCode.NotFound);
        var list = await Body(await owner.GetAsync("/api/v2/workspaces")); Assert.Equal(2, list.GetProperty("totalCount").GetInt32());
        await using var db = fixture.CreateContext();
        Assert.All(await db.Workspaces.ToArrayAsync(), w => Assert.Null(w.LegacyOwnerId));
        Assert.Equal(2, await db.WorkspaceMemberships.CountAsync(m => m.UserId == host.Users[0].Id && m.Role == WorkspaceRole.Owner && m.State == WorkspaceMembershipState.Active));
        var membership = await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == long.Parse(id));
        membership.State = WorkspaceMembershipState.Suspended; await db.SaveChangesAsync();
        await Problem(await owner.GetAsync(path), HttpStatusCode.NotFound);
        Assert.Equal(1, (await Body(await owner.GetAsync("/api/v2/workspaces"))).GetProperty("totalCount").GetInt32());
    }

    [MySqlFact]
    public async Task FailedOwnerMembershipCreationRollsBackTheCompany()
    {
        var fail = new FailCompanyMembership();
        await using var host = new ProjectHost(fixture, true, true, fail); await host.InitializeAsync();
        using var owner = await host.LoginAsync(0); fail.Enabled = true;
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, "/api/v2/workspaces", new { name = "Must roll back" }), HttpStatusCode.InternalServerError);
        await using var db = fixture.CreateContext(); Assert.Equal(0, await db.Workspaces.CountAsync()); Assert.Equal(0, await db.WorkspaceMemberships.CountAsync());
    }

    private sealed class FailCompanyMembership : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<WorkspaceMembership>().Any(e => e.State == EntityState.Added))
                throw new InvalidOperationException("Synthetic membership persistence failure.");
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }
}
