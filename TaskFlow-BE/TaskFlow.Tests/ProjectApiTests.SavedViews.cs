using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private static object ViewDefinition(string name="My queue",bool shared=false,string surface="work",string? projectId=null,Dictionary<string,string>? filters=null)=>new{name,shared,surface,projectId,filters=filters??new Dictionary<string,string>{{"queue","overdue"},{"assignment","mine"}}};
    private async Task<JsonElement> CreateView(ProjectHost h,HttpClient u,Workspace w,object? definition=null)=>await Body(await h.MemberWriteAsync(u,HttpMethod.Post,W(w)+"/views",definition??ViewDefinition()));
    [MySqlFact]
    public async Task SavedViewsKeepPersonalDefinitionsPrivateAndSharedViewsDoNotGrantProjects()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var owner=await h.LoginAsync(0);using var admin=await h.LoginAsync(2);
        var personal=await CreateView(h,owner,c.A);var id=personal.GetProperty("id").GetString();
        await Problem(await admin.GetAsync(W(c.A)+"/views/"+id),HttpStatusCode.NotFound);Assert.Equal(0,(await Body(await admin.GetAsync(W(c.A)+"/views"))).GetProperty("totalCount").GetInt32());
        var shared=await CreateView(h,owner,c.A,ViewDefinition("Private project view",true,"tasks",c.A1.Id.ToString(),new(){{"priority","High"}}));
        await Problem(await admin.GetAsync(W(c.A)+"/views/"+shared.GetProperty("id").GetString()),HttpStatusCode.NotFound);
        var global=await CreateView(h,owner,c.A,ViewDefinition("Shared overdue",true));Assert.True((await Body(await admin.GetAsync(W(c.A)+"/views/"+global.GetProperty("id").GetString()))).GetProperty("canManage").GetBoolean());
        await Problem(await owner.GetAsync(W(c.B)+"/views/"+id),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task SavedViewsEnforceSharedManagementAndVersionedUpdatesAndDeletes()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var guest=await h.LoginAsync(0);using var ownerB=await h.LoginAsync(1);
        await Problem(await h.MemberWriteAsync(guest,HttpMethod.Post,W(c.B)+"/views",ViewDefinition(shared:true)),HttpStatusCode.Forbidden);
        var v=await CreateView(h,guest,c.B);var path=W(c.B)+"/views/"+v.GetProperty("id").GetString();
        var updated=await Body(await h.MemberWriteAsync(guest,HttpMethod.Put,path,new{name="Updated",surface="work",shared=false,projectId=(string?)null,filters=new Dictionary<string,string>{{"queue","all"}},expectedVersion="1"}));Assert.Equal("2",updated.GetProperty("version").GetString());
        await Problem(await h.MemberWriteAsync(guest,HttpMethod.Delete,path,new{expectedVersion="1"}),HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent,(await h.MemberWriteAsync(guest,HttpMethod.Delete,path,new{expectedVersion="2"})).StatusCode);await Problem(await guest.GetAsync(path),HttpStatusCode.NotFound);
        var shared=await CreateView(h,ownerB,c.B,ViewDefinition(shared:true));await Problem(await h.MemberWriteAsync(guest,HttpMethod.Delete,W(c.B)+"/views/"+shared.GetProperty("id").GetString(),new{expectedVersion="1"}),HttpStatusCode.Forbidden);
    }
    [MySqlFact]
    public async Task SavedViewDefaultsAreSelfScopedVersionedAndUnavailableAfterDeletion()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var owner=await h.LoginAsync(0);using var admin=await h.LoginAsync(2);var v=await CreateView(h,owner,c.A,ViewDefinition(shared:true));var id=v.GetProperty("id").GetString();var path=W(c.A)+"/view-preferences/work";
        Assert.Equal("0",(await Body(await owner.GetAsync(path))).GetProperty("version").GetString());
        var saved=await Body(await h.MemberWriteAsync(owner,HttpMethod.Put,path,new{savedViewId=id,expectedVersion="0"}));Assert.Equal("1",saved.GetProperty("version").GetString());
        Assert.Equal(JsonValueKind.Null,(await Body(await admin.GetAsync(path))).GetProperty("savedViewId").ValueKind);
        await Problem(await h.MemberWriteAsync(owner,HttpMethod.Put,path,new{savedViewId=id,expectedVersion="0"}),HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent,(await h.MemberWriteAsync(owner,HttpMethod.Delete,W(c.A)+"/views/"+id,new{expectedVersion="1"})).StatusCode);
        var missing=await Body(await owner.GetAsync(path));Assert.False(missing.GetProperty("available").GetBoolean());Assert.Equal(JsonValueKind.Null,missing.GetProperty("savedViewId").ValueKind);
        Assert.True((await Body(await h.MemberWriteAsync(owner,HttpMethod.Put,path,new{savedViewId=(string?)null,expectedVersion="1"}))).GetProperty("available").GetBoolean());
    }
    [MySqlFact]
    public async Task SavedViewsReauthorizeDeletedProjectsMembershipAndChangedTargets()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var guest=await h.LoginAsync(0);using var owner=await h.LoginAsync(1);
        var v=await CreateView(h,guest,c.B,ViewDefinition("Project list",false,"tasks",c.B1.Id.ToString(),new()));var id=v.GetProperty("id").GetString();var pref=W(c.B)+"/view-preferences/tasks:"+c.B1.Id;
        await Body(await h.MemberWriteAsync(guest,HttpMethod.Put,pref,new{savedViewId=id,expectedVersion="0"}));
        await using(var db=fixture.CreateContext()){var member=await db.ProjectMembers.SingleAsync(m=>m.ProjectId==c.B1.Id&&m.UserId==h.Users[0].Id);member.RevokedAtUtc=h.Clock.Now.UtcDateTime;await db.SaveChangesAsync();}
        await Problem(await guest.GetAsync(W(c.B)+"/views/"+id),HttpStatusCode.NotFound);await Problem(await guest.GetAsync(pref),HttpStatusCode.NotFound);
        var shared=await CreateView(h,owner,c.B,ViewDefinition("Board",true,"board",c.B1.Id.ToString(),new()));
        await using(var db=fixture.CreateContext()){var project=await db.Projects.IgnoreQueryFilters().SingleAsync(p=>p.Id==c.B1.Id);project.DeletedAtUtc=h.Clock.Now.UtcDateTime;await db.SaveChangesAsync();}
        await Problem(await owner.GetAsync(W(c.B)+"/views/"+shared.GetProperty("id").GetString()),HttpStatusCode.NotFound);
        await using(var db=fixture.CreateContext()){await db.WorkspaceMemberships.Where(m=>m.WorkspaceId==c.B.Id&&m.UserId==h.Users[0].Id).ExecuteUpdateAsync(x=>x.SetProperty(m=>m.State,WorkspaceMembershipState.Suspended));}
        await Problem(await guest.GetAsync(W(c.B)+"/views?pageSize=invalid"),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task SavedViewsRejectUnsupportedDeletedFieldsAndUnsafeOrInvertedFilters()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var user=await h.LoginAsync(0);
        foreach(var filters in new[]{new Dictionary<string,string>{{"unknown","x"}},new(){{"page","2"}},new(){{"queue","Due"}},new(){{"search",new string('x',201)}},new(){{"fromDate","2026-02-30"}},new(){{"fromDate","2026-11-17"},{"throughDate","2026-11-16"}},new(){{"fromUtc","2026-11-16"}},new(){{"fromDate","2026-11-16"},{"fromUtc","2026-11-16T00:00:00Z"}}})await Problem(await h.MemberWriteAsync(user,HttpMethod.Post,W(c.A)+"/views",ViewDefinition(filters:filters)),HttpStatusCode.BadRequest);
        await Problem(await h.MemberWriteAsync(user,HttpMethod.Post,W(c.A)+"/views",ViewDefinition(surface:"board",projectId:c.A1.Id.ToString())),HttpStatusCode.BadRequest);
        await Problem(await h.MemberWriteAsync(user,HttpMethod.Post,W(c.A)+"/views",ViewDefinition(surface:"tasks",projectId:c.HiddenB.Id.ToString(),filters:new())),HttpStatusCode.NotFound);
        await Problem(await user.GetAsync(W(c.A)+"/views?page=1&page=2"),HttpStatusCode.BadRequest);
        await Problem(await user.GetAsync(W(c.A)+"/view-preferences/invalid"),HttpStatusCode.BadRequest);
    }
    [MySqlFact]
    public async Task SavedViewsKeepTruthfulPagingAndEnforceBoundedPerMemberStorage()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var user=await h.LoginAsync(0);
        await using(var db=fixture.CreateContext()){for(var i=0;i<100;i++)db.SavedViews.Add(new(){WorkspaceId=c.A.Id,UserId=h.Users[0].Id,Name="View "+i.ToString("D3")});await db.SaveChangesAsync();}
        var p=await Body(await user.GetAsync(W(c.A)+"/views?page=2&pageSize=60"));Assert.Equal(100,p.GetProperty("totalCount").GetInt32());Assert.Equal(40,p.GetProperty("items").GetArrayLength());
        await Problem(await h.MemberWriteAsync(user,HttpMethod.Post,W(c.A)+"/views",ViewDefinition()),HttpStatusCode.BadRequest);
        Assert.Empty((await Body(await user.GetAsync(W(c.A)+"/views?page=50"))).GetProperty("items").EnumerateArray());
    }
    [MySqlFact]
    public async Task SavedViewsRacePreferenceCreationWithoutOverwritingAndAuditSharedChangesWithoutPrivateText()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var first=await h.LoginAsync(0);using var second=await h.LoginAsync(0);
        var v=await CreateView(h,first,c.A,ViewDefinition("Private title canary",true));var id=v.GetProperty("id").GetString();var path=W(c.A)+"/view-preferences/work";
        var results=await Task.WhenAll(h.MemberWriteAsync(first,HttpMethod.Put,path,new{savedViewId=id,expectedVersion="0"}),h.MemberWriteAsync(second,HttpMethod.Put,path,new{savedViewId=id,expectedVersion="0"}));Assert.Equal(new[]{HttpStatusCode.OK,HttpStatusCode.Conflict},results.Select(r=>r.StatusCode).OrderBy(x=>(int)x));
        await using var db=fixture.CreateContext();var audit=await db.WorkspaceAuditLogs.SingleAsync(a=>a.WorkspaceId==c.A.Id&&a.Action=="SavedViewCreated");Assert.Equal("SavedView",audit.SubjectType);Assert.Equal(id,audit.SubjectId);Assert.DoesNotContain("canary",audit.ChangesJson);Assert.True(await db.OutboxEvents.AnyAsync(e=>e.WorkspaceId==c.A.Id));
    }
    [MySqlFact]
    public async Task SavedViewsRejectMismatchedDefaultsAndInvalidateDefaultsAfterSurfaceChanges()
    {
        await using var h=new ProjectHost(fixture,true,true,true);await h.InitializeAsync();var c=await SeedCompanies(h);using var user=await h.LoginAsync(0);var v=await CreateView(h,user,c.A);var id=v.GetProperty("id").GetString();
        await Problem(await h.MemberWriteAsync(user,HttpMethod.Put,W(c.A)+"/view-preferences/board:"+c.A1.Id,new{savedViewId=id,expectedVersion="0"}),HttpStatusCode.BadRequest);
        await Body(await h.MemberWriteAsync(user,HttpMethod.Put,W(c.A)+"/view-preferences/work",new{savedViewId=id,expectedVersion="0"}));
        await Body(await h.MemberWriteAsync(user,HttpMethod.Put,W(c.A)+"/views/"+id,new{name="Changed target",surface="board",projectId=c.A1.Id.ToString(),shared=false,filters=new Dictionary<string,string>(),expectedVersion="1"}));
        Assert.False((await Body(await user.GetAsync(W(c.A)+"/view-preferences/work"))).GetProperty("available").GetBoolean());
    }
}
