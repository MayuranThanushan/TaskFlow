using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private static string BoardPath(Companies c)=>W(c.A)+"/projects/"+c.A1.Id+"/board";
    [MySqlFact]
    public async Task BoardCountsAndIndependentColumnsEnumerateMoreThan100TasksPerStatus()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        await using(var db=fixture.CreateContext(host.Clock))
        {
            for(var state=0;state<3;state++)for(var i=0;i<121;i++)db.Tasks.Add(new(){WorkspaceId=c.A.Id,ProjectId=c.A1.Id,Title="Board "+i,Status=(TaskState)state});
            db.Tasks.Add(new(){WorkspaceId=c.A.Id,ProjectId=c.A1.Id,Title="Deleted board canary",DeletedAtUtc=host.Clock.Now.UtcDateTime});await db.SaveChangesAsync();
        }
        var board=await Body(await owner.GetAsync(BoardPath(c)));Assert.Equal(c.A1.Id.ToString(),board.GetProperty("projectId").GetString());Assert.Equal(364,board.GetProperty("totalCount").GetInt32());
        var columns=board.GetProperty("columns").EnumerateArray().ToArray();Assert.Equal(new[]{"ToDo","InProgress","Done"},columns.Select(x=>x.GetProperty("status").GetString()));
        var all=new HashSet<string>();
        foreach(var column in columns)
        {
            var state=column.GetProperty("status").GetString();var total=column.GetProperty("totalCount").GetInt32();Assert.Equal(state=="ToDo"?122:121,total);
            var first=await Body(await owner.GetAsync(BoardPath(c)+"/columns/"+state+"?pageSize=100"));var second=await Body(await owner.GetAsync(BoardPath(c)+"/columns/"+state+"?pageSize=100&page=2"));
            Assert.Equal(total,first.GetProperty("totalCount").GetInt32());Assert.Equal(2,first.GetProperty("totalPages").GetInt32());Assert.Equal(100,first.GetProperty("items").GetArrayLength());Assert.Equal(total-100,second.GetProperty("items").GetArrayLength());
            var items=first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).ToArray();
            Assert.All(items,t=>{Assert.Equal(state,t.GetProperty("status").GetString());Assert.True(all.Add(t.GetProperty("id").GetString()!));Assert.NotNull(t.GetProperty("version").GetString());});
            var list=await Body(await owner.GetAsync(W(c.A)+"/projects/"+c.A1.Id+"/tasks?status="+state+"&pageSize=100"));Assert.Equal(first.ToString(),list.ToString());
            var ids=items.Select(t=>long.Parse(t.GetProperty("id").GetString()!)).ToArray();Assert.Equal(ids.OrderDescending(),ids);
        }
        Assert.Equal(364,all.Count);
    }
    [MySqlFact]
    public async Task BoardFiltersCountsAndColumnSortingMatchTaskListWithEmptyColumns()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        await using(var db=fixture.CreateContext(host.Clock))
        {
            db.Tasks.AddRange(new TaskItem{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,Title="Needle B",Priority=TaskPriority.High,AssigneeId=host.Users[0].Id,DueDateUtc=host.Clock.Now.UtcDateTime},
                new TaskItem{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,Title="Needle A",Priority=TaskPriority.High,AssigneeId=host.Users[0].Id,Status=TaskState.InProgress},
                new TaskItem{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,Title="Needle excluded",Priority=TaskPriority.Low});await db.SaveChangesAsync();
        }
        var filter="search=%20Needle%20&priority=High&assigneeId="+host.Users[0].Id;var board=await Body(await owner.GetAsync(BoardPath(c)+"?"+filter));Assert.Equal(2,board.GetProperty("totalCount").GetInt32());
        Assert.Equal(new[]{1,1,0},board.GetProperty("columns").EnumerateArray().Select(c=>c.GetProperty("totalCount").GetInt32()));
        foreach(var sort in new[]{"title","priority","dueDateUtc","createdAtUtc"})foreach(var direction in new[]{"asc","desc"})
        {
            var suffix=filter+"&sortBy="+sort+"&sortDirection="+direction;
            var column=await Body(await owner.GetAsync(BoardPath(c)+"/columns/ToDo?"+suffix));var list=await Body(await owner.GetAsync(W(c.A)+"/projects/"+c.A1.Id+"/tasks?status=ToDo&"+suffix));Assert.Equal(list.ToString(),column.ToString());
        }
        var empty=await Body(await owner.GetAsync(BoardPath(c)+"?search=NoSuchBoardTask"));Assert.Equal(0,empty.GetProperty("totalCount").GetInt32());Assert.Equal(3,empty.GetProperty("columns").GetArrayLength());
        var far=await Body(await owner.GetAsync(BoardPath(c)+"/columns/Done?page=2147483647&pageSize=1"));Assert.Empty(far.GetProperty("items").EnumerateArray());Assert.Equal(0,far.GetProperty("totalCount").GetInt32());
    }
    [MySqlFact]
    public async Task BoardSharesCompanyPrivateProjectGuestAndRevocationAccessRules()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);using var admin=await host.LoginAsync(2);using var stranger=await host.LoginAsync(1);using var anonymous=host.Client();
        foreach(var suffix in new[]{"","/columns/ToDo"})
        {
            var path=BoardPath(c)+suffix;await Problem(await anonymous.GetAsync(path),HttpStatusCode.Unauthorized);await Problem(await admin.GetAsync(path),HttpStatusCode.NotFound);await Problem(await stranger.GetAsync(path),HttpStatusCode.NotFound);
            await Problem(await owner.GetAsync(W(c.B)+"/projects/"+c.A1.Id+"/board"+suffix),HttpStatusCode.NotFound);
            Assert.Equal(HttpStatusCode.OK,(await owner.GetAsync(W(c.B)+"/projects/"+c.B1.Id+"/board"+suffix)).StatusCode);
        }
        await GrantNotificationRecipient(host,c);Assert.Equal(HttpStatusCode.OK,(await admin.GetAsync(BoardPath(c))).StatusCode);
        await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Role,MembershipRole.Viewer));
        Assert.Equal(HttpStatusCode.OK,(await admin.GetAsync(BoardPath(c)+"/columns/ToDo")).StatusCode);
        await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.RevokedAtUtc,DateTime.UtcNow));
        await Problem(await admin.GetAsync(BoardPath(c)),HttpStatusCode.NotFound);
        await using(var db=fixture.CreateContext())await db.WorkspaceMemberships.Where(m=>m.WorkspaceId==c.A.Id&&m.UserId==host.Users[0].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.State,WorkspaceMembershipState.Suspended));
        await Problem(await owner.GetAsync(BoardPath(c)+"?priority=INVALID"),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task BoardRejectsUnknownRepeatedAndInvalidQueryValuesWithoutChangingData()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        foreach(var suffix in new[]{"?status=ToDo","?page=1","?sortBy=title","?priority=","?priority=0","?assigneeId=","?assigneeId=9223372036854775808","?search="+new string('x',201),"?priority=High&Priority=Low","?search=a&search=b","?actorId=1"})await Problem(await owner.GetAsync(BoardPath(c)+suffix),HttpStatusCode.BadRequest);
        foreach(var suffix in new[]{"?page=0","?pageSize=101","?page=2147483647&pageSize=2","?sortBy=Title","?sortDirection=ASC","?status=Done","?page=1&Page=2","?priority=High&priority=Low","?search="+new string('x',201)})await Problem(await owner.GetAsync(BoardPath(c)+"/columns/ToDo"+suffix),HttpStatusCode.BadRequest);
        foreach(var state in new[]{"todo","0","ToDo,Done","Unknown"})await Problem(await owner.GetAsync(BoardPath(c)+"/columns/"+state),HttpStatusCode.BadRequest);
        await using var db=fixture.CreateContext();Assert.False(await db.OutboxEvents.AnyAsync(e=>e.WorkspaceId==c.A.Id));
    }
    [MySqlFact]
    public async Task BoardHidesDeletedParentsAndDisabledPreviewAndHasNoLegacyEndpoint()
    {
        await using(var disabled=new ProjectHost(fixture)){await disabled.InitializeAsync();var c=await SeedCompanies(disabled);using var owner=await disabled.LoginAsync(0);await Problem(await owner.GetAsync(BoardPath(c)),HttpStatusCode.NotFound);await Problem(await owner.GetAsync(BoardPath(c)+"/columns/ToDo"),HttpStatusCode.NotFound);}
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var company=await SeedCompanies(host);using var reader=await host.LoginAsync(0);
        await using(var db=fixture.CreateContext())await db.Projects.IgnoreQueryFilters().Where(p=>p.Id==company.A1.Id).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.DeletedAtUtc,DateTime.UtcNow));
        await Problem(await reader.GetAsync(BoardPath(company)),HttpStatusCode.NotFound);await Problem(await reader.GetAsync(BoardPath(company)+"/columns/ToDo"),HttpStatusCode.NotFound);await Problem(await reader.GetAsync("/api/v1/projects/"+company.A1.Id+"/board"),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task BoardColumnCountAndPageKeepOneSnapshotAcrossConcurrentCreation()
    {
        var gate=new TaskCountGate();await using var host=new ProjectHost(fixture,true,true,true,gate);await host.InitializeAsync();var c=await SeedCompanies(host);using var reader=await host.LoginAsync(0);using var writer=await host.LoginAsync(0);
        gate.Armed=true;var pending=reader.GetAsync(BoardPath(c)+"/columns/ToDo");string created;
        try {await gate.Counted.Task.WaitAsync(TimeSpan.FromSeconds(10));gate.Armed=false;created=(await Body(await host.MemberWriteAsync(writer,HttpMethod.Post,W(c.A)+"/projects/"+c.A1.Id+"/tasks",new{title="Concurrent board card"}))).GetProperty("id").GetString()!;}
        finally {gate.Armed=false;gate.Release.TrySetResult();}
        var snapshot=await Body(await pending.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Equal(1,snapshot.GetProperty("totalCount").GetInt32());Assert.Single(snapshot.GetProperty("items").EnumerateArray());Assert.DoesNotContain(snapshot.GetProperty("items").EnumerateArray(),t=>t.GetProperty("id").GetString()==created);
        var fresh=await Body(await reader.GetAsync(BoardPath(c)));Assert.Equal(2,fresh.GetProperty("totalCount").GetInt32());
    }
}
