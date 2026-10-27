using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    private sealed record Companies(Workspace A, Workspace B, Project A1, Project A2, Project B1, Project HiddenB, TaskItem TaskA, TaskItem TaskB, TaskItem DeletedB);
    private async Task<Companies> SeedCompanies(ProjectHost host)
    {
        await using var db = fixture.CreateContext(host.Clock);
        var a = new Workspace { Name = "Company A", LegacyOwnerId = host.Users[0].Id };
        var b = new Workspace { Name = "Company B", LegacyOwnerId = host.Users[1].Id };
        db.Workspaces.AddRange(a, b); await db.SaveChangesAsync();
        db.WorkspaceMemberships.AddRange(
            new WorkspaceMembership { WorkspaceId = a.Id, UserId = host.Users[0].Id, Role = WorkspaceRole.Owner },
            new WorkspaceMembership { WorkspaceId = a.Id, UserId = host.Users[2].Id, Role = WorkspaceRole.Admin },
            new WorkspaceMembership { WorkspaceId = b.Id, UserId = host.Users[1].Id, Role = WorkspaceRole.Owner },
            new WorkspaceMembership { WorkspaceId = b.Id, UserId = host.Users[0].Id, Role = WorkspaceRole.Guest });
        var a1 = new Project { Name = "A first", OwnerId = host.Users[0].Id, WorkspaceId = a.Id };
        var a2 = new Project { Name = "A second", OwnerId = host.Users[0].Id, WorkspaceId = a.Id };
        var b1 = new Project { Name = "B shared", OwnerId = host.Users[1].Id, WorkspaceId = b.Id };
        var hidden = new Project { Name = "B private canary", OwnerId = host.Users[1].Id, WorkspaceId = b.Id };
        db.Projects.AddRange(a1, a2, b1, hidden); await db.SaveChangesAsync();
        db.ProjectMembers.Add(new ProjectMember { WorkspaceId = b.Id, ProjectId = b1.Id, UserId = host.Users[0].Id });
        var ta = new TaskItem { ProjectId = a1.Id, WorkspaceId = a.Id, Title = "A task", AssigneeId = host.Users[0].Id, DueDateUtc = host.Clock.Now.UtcDateTime.AddDays(-1) };
        var tb = new TaskItem { ProjectId = b1.Id, WorkspaceId = b.Id, Title = "B task", AssigneeId = host.Users[0].Id, Status = TaskState.InProgress, Priority = TaskPriority.High };
        var deleted = new TaskItem { ProjectId = b1.Id, WorkspaceId = b.Id, Title = "Deleted history", DeletedAtUtc = host.Clock.Now.UtcDateTime, AssigneeId = host.Users[2].Id };
        db.Tasks.AddRange(ta, tb, deleted, new TaskItem { ProjectId = a2.Id, WorkspaceId = a.Id, Title = "A completed", Status = TaskState.Done },
            new TaskItem { ProjectId = hidden.Id, WorkspaceId = b.Id, Title = "Hidden B task canary" });
        await db.SaveChangesAsync();
        db.ActivityLogs.AddRange(new ActivityLog { WorkspaceId = a.Id, ProjectId = a1.Id, TaskId = ta.Id, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.TaskCreated },
            new ActivityLog { WorkspaceId = b.Id, ProjectId = b1.Id, TaskId = deleted.Id, ActorUserId = host.Users[2].Id, ActionType = ActivityAction.TaskDeleted, Details = "Private raw history canary" },
            new ActivityLog { WorkspaceId = b.Id, ProjectId = hidden.Id, ActorUserId = host.Users[1].Id, ActionType = ActivityAction.ProjectCreated });
        await db.SaveChangesAsync();
        return new(a, b, a1, a2, b1, hidden, ta, tb, deleted);
    }
    private static string W(Workspace workspace) => "/api/v2/workspaces/" + workspace.Id;

    [MySqlFact]
    public async Task WorkspaceReadsAreDisabledByDefaultAndUnauthenticatedRequestsStillRequireLogin()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync();
        using var user = await host.LoginAsync(0); using var anonymous = host.Client();
        await Problem(await user.GetAsync("/api/v2/workspaces"), HttpStatusCode.NotFound);
        await Problem(await user.GetAsync("/api/v2/workspaces/1/projects?pageSize=invalid"), HttpStatusCode.NotFound);
        await Problem(await anonymous.GetAsync("/api/v2/workspaces"), HttpStatusCode.Unauthorized);
        Assert.Equal(0, (await Body(await user.GetAsync("/api/v1/projects"))).GetProperty("totalCount").GetInt32());
    }

    [MySqlFact]
    public async Task SharedIdentityCountsListsAndActivityRemainInTheirExplicitWorkspace()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var user = await host.LoginAsync(0);
        var workspaces = await Body(await user.GetAsync("/api/v2/workspaces?pageSize=1"));
        Assert.Equal(2, workspaces.GetProperty("totalCount").GetInt32()); Assert.Equal(2, workspaces.GetProperty("totalPages").GetInt32());
        Assert.Equal(c.A.Id.ToString(), workspaces.GetProperty("items")[0].GetProperty("id").GetString());
        var a = await Body(await user.GetAsync(W(c.A) + "/projects?pageSize=1&sortBy=name&sortDirection=asc"));
        Assert.Equal(2, a.GetProperty("totalCount").GetInt32()); Assert.Equal(c.A1.Id.ToString(), a.GetProperty("items")[0].GetProperty("id").GetString());
        var b = await Body(await user.GetAsync(W(c.B) + "/projects")); Assert.Equal(1, b.GetProperty("totalCount").GetInt32());
        Assert.Equal(c.B1.Id.ToString(), b.GetProperty("items")[0].GetProperty("id").GetString());
        var dashA = await Body(await user.GetAsync(W(c.A) + "/dashboard")); var dashB = await Body(await user.GetAsync(W(c.B) + "/dashboard"));
        Assert.Equal(2, dashA.GetProperty("totalTasks").GetInt32()); Assert.Equal(1, dashA.GetProperty("overdueTasks").GetInt32());
        Assert.Equal(1, dashB.GetProperty("totalTasks").GetInt32()); Assert.Equal(1, dashB.GetProperty("assignedToMe").GetInt32());
        var activity = await Body(await user.GetAsync(W(c.B) + "/activity")); Assert.Equal(1, activity.GetProperty("totalCount").GetInt32());
        Assert.Equal(c.DeletedB.Id.ToString(), activity.GetProperty("items")[0].GetProperty("taskId").GetString());
        Assert.DoesNotContain("Private raw history", activity.ToString()); Assert.DoesNotContain("canary", b.ToString());
        var response = await user.GetAsync(W(c.B) + "/projects/" + c.B1.Id);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(1, (await Body(response)).GetProperty("totalTasks").GetInt32());
    }

    [MySqlFact]
    public async Task MixedWorkspaceResourceIdsAndPrivateProjectsAreDeniedAcrossEveryReader()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var user = await host.LoginAsync(0); using var outsider = await host.LoginAsync(2);
        foreach (var suffix in new[] { "/projects/" + c.B1.Id, "/projects/" + c.B1.Id + "/tasks", "/projects/" + c.B1.Id + "/members", "/projects/" + c.B1.Id + "/activity", "/tasks/" + c.TaskB.Id })
            await Problem(await user.GetAsync(W(c.A) + suffix), HttpStatusCode.NotFound);
        foreach (var suffix in new[] { "/projects", "/dashboard", "/activity", "/tasks/" + c.TaskB.Id, "/projects/" + c.B1.Id + "/members" })
            await Problem(await outsider.GetAsync(W(c.B) + suffix), HttpStatusCode.NotFound);
        await Problem(await user.GetAsync(W(c.B) + "/projects/" + c.HiddenB.Id), HttpStatusCode.NotFound);
        await Problem(await outsider.GetAsync(W(c.B) + "/projects?pageSize=broken"), HttpStatusCode.NotFound);
        await Problem(await outsider.GetAsync("/api/v2/workspaces/9223372036854775807/projects?page=broken"), HttpStatusCode.NotFound);
    }

    [MySqlFact]
    public async Task CompanyAdminHasNoImplicitPrivateProjectReadAndRemovedOrSuspendedMembershipLosesAccess()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var admin = await host.LoginAsync(2); using var shared = await host.LoginAsync(0);
        Assert.Equal(0, (await Body(await admin.GetAsync(W(c.A) + "/projects"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await admin.GetAsync(W(c.A) + "/dashboard"))).GetProperty("totalTasks").GetInt32());
        await Problem(await admin.GetAsync(W(c.A) + "/projects/" + c.A1.Id), HttpStatusCode.NotFound);
        await using var db = fixture.CreateContext();
        var membership = await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.B.Id && m.UserId == host.Users[0].Id);
        membership.State = WorkspaceMembershipState.Suspended; await db.SaveChangesAsync();
        foreach (var suffix in new[] { "/projects", "/dashboard", "/activity", "/projects/" + c.B1.Id + "/tasks" })
            await Problem(await shared.GetAsync(W(c.B) + suffix), HttpStatusCode.NotFound);
        Assert.Equal(1, (await Body(await shared.GetAsync("/api/v2/workspaces"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await Body(await shared.GetAsync(W(c.A) + "/projects"))).GetProperty("totalCount").GetInt32());
        db.WorkspaceMemberships.Remove(membership); await db.SaveChangesAsync();
        await Problem(await shared.GetAsync(W(c.B) + "/projects"), HttpStatusCode.NotFound);
    }

    [MySqlFact]
    public async Task MalformedNullableChildScopeCannotLeakThroughCountsTasksMembersOrHistory()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var user = await host.LoginAsync(0); await using var db = fixture.CreateContext();
        var corruptTask = new TaskItem { ProjectId = c.A1.Id, WorkspaceId = c.B.Id, Title = "Wrong workspace canary" };
        var unscopedTask = new TaskItem { ProjectId = c.A1.Id, Title = "Unscoped canary" };
        db.Tasks.AddRange(corruptTask, unscopedTask, new TaskItem { ProjectId = c.A1.Id, WorkspaceId = c.A.Id, Title = "Foreign assignee canary", AssigneeId = host.Users[1].Id });
        db.ProjectMembers.Add(new ProjectMember { ProjectId = c.A1.Id, WorkspaceId = c.B.Id, UserId = host.Users[1].Id });
        await db.SaveChangesAsync();
        db.ActivityLogs.AddRange(new ActivityLog { ProjectId = c.A1.Id, WorkspaceId = c.B.Id, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.TaskCreated },
            new ActivityLog { ProjectId = c.A1.Id, WorkspaceId = c.A.Id, TaskId = c.TaskB.Id, ActorUserId = host.Users[0].Id, ActionType = ActivityAction.TaskCreated });
        await db.SaveChangesAsync();
        var tasks = await Body(await user.GetAsync(W(c.A) + "/projects/" + c.A1.Id + "/tasks"));
        Assert.Equal(1, tasks.GetProperty("totalCount").GetInt32()); Assert.DoesNotContain("canary", tasks.ToString());
        Assert.Equal(1, (await Body(await user.GetAsync(W(c.A) + "/projects/" + c.A1.Id))).GetProperty("totalTasks").GetInt32());
        Assert.Equal(2, (await Body(await user.GetAsync(W(c.A) + "/dashboard"))).GetProperty("totalTasks").GetInt32());
        Assert.Equal(1, (await Body(await user.GetAsync(W(c.A) + "/projects/" + c.A1.Id + "/members"))).GetProperty("items").GetArrayLength());
        Assert.Equal(1, (await Body(await user.GetAsync(W(c.A) + "/activity"))).GetProperty("totalCount").GetInt32());
        await Problem(await user.GetAsync(W(c.A) + "/tasks/" + corruptTask.Id), HttpStatusCode.NotFound);
        await Problem(await user.GetAsync(W(c.A) + "/tasks/" + unscopedTask.Id), HttpStatusCode.NotFound);
    }

    [MySqlFact]
    public async Task CompanyOwnerCannotReadAnotherOwnersPrivateProjectAndUnmappedOwnerRowsFailClosed()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var companyOwner = await host.LoginAsync(0); using var projectOwner = await host.LoginAsync(2);
        await using var db = fixture.CreateContext();
        var privateProject = new Project { WorkspaceId = c.A.Id, OwnerId = host.Users[2].Id, Name = "Admin-owned private canary" };
        var inconsistentOwner = new Project { WorkspaceId = c.A.Id, OwnerId = host.Users[1].Id, Name = "Foreign owner canary" };
        var unscoped = new Project { OwnerId = host.Users[0].Id, Name = "Legacy unscoped canary" };
        db.Projects.AddRange(privateProject, inconsistentOwner, unscoped); await db.SaveChangesAsync();
        db.ProjectMembers.Add(new ProjectMember { WorkspaceId = c.A.Id, ProjectId = inconsistentOwner.Id, UserId = host.Users[0].Id });
        await db.SaveChangesAsync();
        await Problem(await companyOwner.GetAsync(W(c.A) + "/projects/" + privateProject.Id), HttpStatusCode.NotFound);
        await Problem(await companyOwner.GetAsync(W(c.A) + "/projects/" + inconsistentOwner.Id), HttpStatusCode.NotFound);
        await Problem(await companyOwner.GetAsync(W(c.A) + "/projects/" + unscoped.Id), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await projectOwner.GetAsync(W(c.A) + "/projects/" + privateProject.Id)).StatusCode);
        var visible = await Body(await companyOwner.GetAsync(W(c.A) + "/projects"));
        Assert.Equal(2, visible.GetProperty("totalCount").GetInt32()); Assert.DoesNotContain("canary", visible.ToString());
    }

    [MySqlFact]
    public async Task DeletedParentsHideEveryScopedReaderWhileWorkspaceMembershipRemains()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var user = await host.LoginAsync(0); await using var db = fixture.CreateContext();
        var project = await db.Projects.SingleAsync(p => p.Id == c.B1.Id); project.DeletedAtUtc = DateTime.UtcNow; await db.SaveChangesAsync();
        foreach (var suffix in new[] { "/projects/" + c.B1.Id, "/projects/" + c.B1.Id + "/tasks", "/projects/" + c.B1.Id + "/members", "/projects/" + c.B1.Id + "/activity", "/tasks/" + c.TaskB.Id })
            await Problem(await user.GetAsync(W(c.B) + suffix), HttpStatusCode.NotFound);
        foreach (var suffix in new[] { "/projects", "/activity" }) Assert.Equal(0, (await Body(await user.GetAsync(W(c.B) + suffix))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await user.GetAsync(W(c.B) + "/dashboard"))).GetProperty("totalTasks").GetInt32());
        Assert.Equal(2, (await Body(await user.GetAsync("/api/v2/workspaces"))).GetProperty("totalCount").GetInt32());
    }

    [MySqlFact]
    public async Task ScopedSortingPagingLiteralSearchAndOpaqueIdsDoNotMixCompanies()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var user = await host.LoginAsync(0); await using var db = fixture.CreateContext();
        var task = new TaskItem { Id = 9007199254900001, ProjectId = c.A1.Id, WorkspaceId = c.A.Id, Title = "100%_READY", Priority = TaskPriority.High };
        db.Tasks.Add(task); await db.SaveChangesAsync();
        var path = W(c.A) + "/projects/" + c.A1.Id + "/tasks";
        var first = await Body(await user.GetAsync(path + "?pageSize=1&sortBy=priority&sortDirection=desc"));
        Assert.Equal(2, first.GetProperty("totalCount").GetInt32()); Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        Assert.Equal(task.Id.ToString(), first.GetProperty("items")[0].GetProperty("id").GetString());
        var second = await Body(await user.GetAsync(path + "?page=2&pageSize=1&sortBy=priority&sortDirection=desc"));
        Assert.Equal(c.TaskA.Id.ToString(), second.GetProperty("items")[0].GetProperty("id").GetString());
        Assert.Equal(1, (await Body(await user.GetAsync(path + "?search=%25_"))).GetProperty("totalCount").GetInt32());
        var dates = await Body(await user.GetAsync(path + "?sortBy=dueDateUtc&sortDirection=asc"));
        Assert.Equal(task.Id.ToString(), dates.GetProperty("items")[1].GetProperty("id").GetString());
        Assert.Equal(0, (await Body(await user.GetAsync(W(c.B) + "/projects/" + c.B1.Id + "/tasks?search=100%25_"))).GetProperty("totalCount").GetInt32());
    }

    [MySqlFact]
    public async Task ScopedFiltersPaginationAndMemberRemovalPreserveBoundaryOnTheNextRequest()
    {
        await using var host = new ProjectHost(fixture, true); await host.InitializeAsync(); var c = await SeedCompanies(host);
        using var user = await host.LoginAsync(0);
        var path = W(c.B) + "/projects/" + c.B1.Id;
        Assert.Equal(1, (await Body(await user.GetAsync(path + "/tasks?status=InProgress&priority=High&assigneeId=" + host.Users[0].Id + "&sortBy=dueDateUtc&sortDirection=asc"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await user.GetAsync(path + "/tasks?search=missing"))).GetProperty("totalCount").GetInt32());
        foreach (var query in new[] { "status=1", "priority=", "assigneeId=", "workspaceId=1", "page=1&page=2", "pageSize=101" })
            await Problem(await user.GetAsync(path + "/tasks?" + query), HttpStatusCode.BadRequest);
        Assert.Equal(2, (await Body(await user.GetAsync(path + "/members"))).GetProperty("items").GetArrayLength());
        await using var db = fixture.CreateContext();
        db.ProjectMembers.Remove(await db.ProjectMembers.SingleAsync(m => m.ProjectId == c.B1.Id && m.UserId == host.Users[0].Id));
        await db.SaveChangesAsync();
        await Problem(await user.GetAsync(path + "/members"), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Body(await user.GetAsync(W(c.B) + "/projects"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await user.GetAsync(W(c.B) + "/activity"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(0, (await Body(await user.GetAsync(W(c.B) + "/dashboard"))).GetProperty("totalTasks").GetInt32());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await host.MemberWriteAsync(user, HttpMethod.Post, W(c.B) + "/projects", new { name = "Cannot create in read-only preview" })).StatusCode);
    }
}
