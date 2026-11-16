using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private async Task<TaskItem> WorkTask(ProjectHost host,Companies c,long? assignee,DateTime? due=null,TaskState status=TaskState.ToDo,long? project=null,string title="Work needle")
    {
        await using var db=fixture.CreateContext(host.Clock);var task=new TaskItem{WorkspaceId=c.A.Id,ProjectId=project??c.A1.Id,AssigneeId=assignee,DueDateUtc=due,Status=status,Title=title};db.Tasks.Add(task);await db.SaveChangesAsync();return task;
    }
    [MySqlFact]
    public async Task MyWorkSpansAuthorizedProjectsWithExplicitUnassignedAndAllSemantics()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        var tomorrow=await WorkTask(host,c,host.Users[0].Id,host.Clock.Now.UtcDateTime.AddDays(1),project:c.A2.Id);var unassigned=await WorkTask(host,c,null,project:c.A1.Id,title:"Unassigned work");
        var mine=await Body(await owner.GetAsync(W(c.A)+"/work"));Assert.Equal(2,mine.GetProperty("totalCount").GetInt32());Assert.Equal(new[]{"A first","A second"},mine.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("projectName").GetString()).Order());
        Assert.All(mine.GetProperty("items").EnumerateArray(),i=>{Assert.Equal(host.Users[0].Id.ToString(),i.GetProperty("task").GetProperty("assigneeId").GetString());Assert.Equal(host.Users[0].DisplayName,i.GetProperty("assigneeName").GetString());Assert.False(i.GetProperty("task").TryGetProperty("description",out _));});
        var all=await Body(await owner.GetAsync(W(c.A)+"/work?assignment=all&queue=all"));Assert.Equal(4,all.GetProperty("totalCount").GetInt32());Assert.Equal(3,all.GetProperty("counts").GetProperty("open").GetInt32());Assert.Equal(1,all.GetProperty("counts").GetProperty("completed").GetInt32());
        var empty=await Body(await owner.GetAsync(W(c.A)+"/work?assignment=unassigned"));Assert.Equal(unassigned.Id.ToString(),Assert.Single(empty.GetProperty("items").EnumerateArray()).GetProperty("task").GetProperty("id").GetString());
        Assert.DoesNotContain(c.TaskB.Id.ToString(),mine.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("task").GetProperty("id").GetString()));
        var other=await Body(await owner.GetAsync(W(c.B)+"/work"));Assert.Equal(c.TaskB.Id.ToString(),Assert.Single(other.GetProperty("items").EnumerateArray()).GetProperty("task").GetProperty("id").GetString());
        Assert.NotEqual(tomorrow.Id,unassigned.Id);
    }
    [MySqlFact]
    public async Task MyWorkUsesOneUtcCutoffWithInclusiveSevenDayDueAndExclusiveOverdueEdges()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var now=host.Clock.Now.UtcDateTime;
        var past=await WorkTask(host,c,host.Users[0].Id,now.AddTicks(-10));var exact=await WorkTask(host,c,host.Users[0].Id,now);var upper=await WorkTask(host,c,host.Users[0].Id,now.AddDays(7));
        await WorkTask(host,c,host.Users[0].Id,now.AddDays(7).AddTicks(10));await WorkTask(host,c,host.Users[0].Id);await WorkTask(host,c,host.Users[0].Id,now.AddDays(-2),TaskState.Done);
        var due=await Body(await owner.GetAsync(W(c.A)+"/work?queue=due"));Assert.Equal(now,due.GetProperty("asOfUtc").GetDateTime());Assert.Equal(now.AddDays(7),due.GetProperty("dueThroughUtc").GetDateTime());Assert.Equal(new[]{exact.Id.ToString(),upper.Id.ToString()},due.GetProperty("items").EnumerateArray().Select(i=>i.GetProperty("task").GetProperty("id").GetString()));
        var overdue=await Body(await owner.GetAsync(W(c.A)+"/work?queue=overdue"));Assert.Equal(2,overdue.GetProperty("totalCount").GetInt32());Assert.Contains(overdue.GetProperty("items").EnumerateArray(),i=>i.GetProperty("task").GetProperty("id").GetString()==past.Id.ToString());Assert.Equal(1,overdue.GetProperty("counts").GetProperty("completed").GetInt32());
        Assert.Equal(6,overdue.GetProperty("counts").GetProperty("open").GetInt32());
    }
    [MySqlFact]
    public async Task MyWorkBoundsPreserveInclusiveOffsetsAndDatabaseMicrosecondPrecision()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var now=host.Clock.Now.UtcDateTime;
        var earlier=await WorkTask(host,c,host.Users[0].Id,now.AddTicks(1234560));var later=await WorkTask(host,c,host.Users[0].Id,now.AddTicks(1234570));await WorkTask(host,c,host.Users[0].Id);
        var prefix=W(c.A)+"/work?queue=all&fromUtc=";string Stamp(DateTime d)=>Uri.EscapeDataString(d.ToString("O"));
        var lower=await Body(await owner.GetAsync(prefix+Stamp(now.AddTicks(1234561))+"&toUtc="+Stamp(now.AddTicks(1234570))));Assert.Equal(later.Id.ToString(),Assert.Single(lower.GetProperty("items").EnumerateArray()).GetProperty("task").GetProperty("id").GetString());
        var upper=await Body(await owner.GetAsync(prefix+Stamp(now)+"&toUtc="+Stamp(now.AddTicks(1234569))));Assert.Equal(earlier.Id.ToString(),Assert.Single(upper.GetProperty("items").EnumerateArray()).GetProperty("task").GetProperty("id").GetString());
        var exact=await WorkTask(host,c,host.Users[0].Id,now);var offset=Uri.EscapeDataString(new DateTimeOffset(now).ToOffset(TimeSpan.FromHours(5.5)).ToString("O"));
        var offsetResult=await Body(await owner.GetAsync(prefix+offset+"&toUtc="+offset));Assert.Equal(exact.Id.ToString(),Assert.Single(offsetResult.GetProperty("items").EnumerateArray()).GetProperty("task").GetProperty("id").GetString());
        Assert.Equal(0,(await Body(await owner.GetAsync(prefix+"9999-12-31T23%3A59%3A59.9999999Z"))).GetProperty("totalCount").GetInt32());
    }
    [MySqlFact]
    public async Task MyWorkSearchAndFiltersNeverWidenVisibilityOrSearchPrivateDescriptions()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        var task=await WorkTask(host,c,host.Users[0].Id,project:c.A2.Id,title:"100% literal needle");await using(var db=fixture.CreateContext()){var row=await db.Tasks.SingleAsync(t=>t.Id==task.Id);row.Description="PRIVATE_DESCRIPTION_CANARY";row.Priority=TaskPriority.High;await db.SaveChangesAsync();}
        var found=await Body(await owner.GetAsync(W(c.A)+"/work?search=100%25&priority=High&status=ToDo&projectId="+c.A2.Id));Assert.Equal(task.Id.ToString(),Assert.Single(found.GetProperty("items").EnumerateArray()).GetProperty("task").GetProperty("id").GetString());
        Assert.Equal(1,(await Body(await owner.GetAsync(W(c.A)+"/work?search=second"))).GetProperty("totalCount").GetInt32());
        foreach(var search in new[]{"PRIVATE_DESCRIPTION_CANARY","B private canary","B shared"})Assert.Equal(0,(await Body(await owner.GetAsync(W(c.A)+"/work?assignment=all&queue=all&search="+Uri.EscapeDataString(search)))).GetProperty("totalCount").GetInt32());
        await Problem(await owner.GetAsync(W(c.A)+"/work?projectId="+c.B1.Id),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task MyWorkPrivateRolesDeletionAndRevocationMatchExistingTaskReads()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var admin=await host.LoginAsync(2);using var owner=await host.LoginAsync(0);
        Assert.Equal(0,(await Body(await admin.GetAsync(W(c.A)+"/work?assignment=all&queue=all"))).GetProperty("totalCount").GetInt32());await Problem(await admin.GetAsync(W(c.A)+"/work?projectId="+c.A1.Id),HttpStatusCode.NotFound);
        await GrantNotificationRecipient(host,c);await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Role,MembershipRole.Viewer));
        Assert.Equal(1,(await Body(await admin.GetAsync(W(c.A)+"/work?assignment=all"))).GetProperty("totalCount").GetInt32());
        await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.RevokedAtUtc,DateTime.UtcNow));
        Assert.Equal(0,(await Body(await admin.GetAsync(W(c.A)+"/work?assignment=all"))).GetProperty("totalCount").GetInt32());
        await using(var db=fixture.CreateContext())await db.Projects.IgnoreQueryFilters().Where(p=>p.Id==c.A1.Id).ExecuteUpdateAsync(s=>s.SetProperty(p=>p.DeletedAtUtc,DateTime.UtcNow));
        Assert.Equal(0,(await Body(await owner.GetAsync(W(c.A)+"/work"))).GetProperty("totalCount").GetInt32());
        await using(var db=fixture.CreateContext())await db.WorkspaceMemberships.Where(m=>m.WorkspaceId==c.A.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.State,WorkspaceMembershipState.Suspended));
        await Problem(await admin.GetAsync(W(c.A)+"/work?queue=INVALID"),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task MyWorkDeterministicPagingNullLastAndFarPagesExposeCompleteCounts()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var now=host.Clock.Now.UtcDateTime;
        var tasks=new List<TaskItem>();await using(var db=fixture.CreateContext(host.Clock)){for(var i=0;i<121;i++){var t=new TaskItem{WorkspaceId=c.A.Id,ProjectId=i%2==0?c.A1.Id:c.A2.Id,AssigneeId=host.Users[0].Id,Title="Tie",DueDateUtc=now};tasks.Add(t);db.Tasks.Add(t);}await db.SaveChangesAsync();}
        var path=W(c.A)+"/work?search=Tie&pageSize=100";var first=await Body(await owner.GetAsync(path));var second=await Body(await owner.GetAsync(path+"&page=2"));Assert.Equal(121,first.GetProperty("totalCount").GetInt32());Assert.Equal(2,first.GetProperty("totalPages").GetInt32());
        Assert.Equal(tasks.OrderByDescending(t=>t.Id).Select(t=>t.Id.ToString()),first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(i=>i.GetProperty("task").GetProperty("id").GetString()));
        var far=await Body(await owner.GetAsync(W(c.A)+"/work?page=2147483647&pageSize=1"));Assert.Empty(far.GetProperty("items").EnumerateArray());Assert.Equal(122,far.GetProperty("totalCount").GetInt32());
        await WorkTask(host,c,host.Users[0].Id,title:"Null deadline");var ordered=await Body(await owner.GetAsync(W(c.A)+"/work?sortBy=dueDateUtc&sortDirection=desc&pageSize=100&page=2"));Assert.Null(ordered.GetProperty("items").EnumerateArray().Last().GetProperty("task").GetProperty("dueDateUtc").GetString());
    }
    [MySqlFact]
    public async Task MyWorkRejectsUnknownRepeatedAndInvalidFiltersAndDisabledRoutes()
    {
        await using(var host=new ProjectHost(fixture,true,true,true)){await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);using var anon=host.Client();await Problem(await anon.GetAsync(W(c.A)+"/work"),HttpStatusCode.Unauthorized);
            foreach(var q in new[]{"queue=Due","assignment=other","assignment=","queue=","userId=1","projectId=","projectId=-1","status=0","priority=High,Low","sortBy=rank","sortDirection=ASC","page=0","pageSize=101","page=2147483647&pageSize=2","page=1&Page=2","queue=open&queue=all","fromUtc=","fromUtc=2026-01-01","fromUtc=2026-02-01T00:00:00Z&toUtc=2026-01-01T00:00:00Z","search="+new string('x',201)})await Problem(await owner.GetAsync(W(c.A)+"/work?"+q),HttpStatusCode.BadRequest);
            await using var db=fixture.CreateContext();Assert.False(await db.OutboxEvents.AnyAsync(e=>e.WorkspaceId==c.A.Id));}
        await using var disabled=new ProjectHost(fixture);await disabled.InitializeAsync();var company=await SeedCompanies(disabled);using var user=await disabled.LoginAsync(0);await Problem(await user.GetAsync(W(company.A)+"/work"),HttpStatusCode.NotFound);
    }
    private sealed class WorkQueryCounter : DbCommandInterceptor
    {
        public bool Armed {get;set;}public int TaskQueries {get;private set;}public int TotalQueries {get;private set;}public void Reset(){TaskQueries=0;TotalQueries=0;}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken t=default){if(Armed){TotalQueries++;if(c.CommandText.Contains("`Tasks`",StringComparison.Ordinal))TaskQueries++;}return ValueTask.FromResult(r);}
    }
    [MySqlFact]
    public async Task MyWorkHasNoPerProjectOrPerAssigneeQueryFanout()
    {
        var counter=new WorkQueryCounter();await using var host=new ProjectHost(fixture,true,true,true,counter);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        for(var i=0;i<5;i++)await WorkTask(host,c,host.Users[0].Id,project:i%2==0?c.A1.Id:c.A2.Id);
        counter.Armed=true;await Body(await owner.GetAsync(W(c.A)+"/work"));counter.Armed=false;Assert.Equal(2,counter.TaskQueries);var first=counter.TotalQueries;
        for(var i=0;i<25;i++)await WorkTask(host,c,host.Users[0].Id,project:i%2==0?c.A1.Id:c.A2.Id);
        counter.Reset();counter.Armed=true;await Body(await owner.GetAsync(W(c.A)+"/work"));counter.Armed=false;Assert.Equal(2,counter.TaskQueries);Assert.Equal(first,counter.TotalQueries);
    }
    [MySqlFact]
    public async Task MyWorkCountsRowsAndClockShareSnapshotDuringConcurrentCreateAndClockAdvance()
    {
        var gate=new TaskCountGate();await using var host=new ProjectHost(fixture,true,true,true,gate);await host.InitializeAsync();var c=await SeedCompanies(host);using var reader=await host.LoginAsync(0);using var writer=await host.LoginAsync(0);var at=host.Clock.Now;var due=await WorkTask(host,c,host.Users[0].Id,at.UtcDateTime.AddMinutes(30));
        gate.Armed=true;var pending=reader.GetAsync(W(c.A)+"/work?queue=due");
        try {await gate.Counted.Task.WaitAsync(TimeSpan.FromSeconds(10));gate.Armed=false;host.Clock.Now=at.AddHours(1);await Body(await host.MemberWriteAsync(writer,HttpMethod.Post,W(c.A)+"/projects/"+c.A1.Id+"/tasks",new{title="Concurrent work",assigneeId=host.Users[0].Id.ToString(),dueDateUtc=host.Clock.Now.AddDays(1).ToString("O")}));}
        finally{gate.Armed=false;gate.Release.TrySetResult();}
        var snapshot=await Body(await pending.WaitAsync(TimeSpan.FromSeconds(10)));Assert.Equal(at.UtcDateTime,snapshot.GetProperty("asOfUtc").GetDateTime());Assert.Equal(due.Id.ToString(),Assert.Single(snapshot.GetProperty("items").EnumerateArray()).GetProperty("task").GetProperty("id").GetString());Assert.Equal(1,snapshot.GetProperty("totalCount").GetInt32());
    }
}
