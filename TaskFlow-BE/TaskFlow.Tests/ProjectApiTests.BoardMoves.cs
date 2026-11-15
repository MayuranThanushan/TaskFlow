using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private static string MovePath(Companies c,long task)=>W(c.A)+"/projects/"+c.A1.Id+"/tasks/"+task+"/move";
    private static object Position(string status,long? previous=null,string? previousVersion=null,long? next=null,string? nextVersion=null)=>new{status,previousTaskId=previous?.ToString(),previousVersion,nextTaskId=next?.ToString(),nextVersion};
    private async Task<TaskItem[]> RankCards(ProjectHost host,Companies c,TaskState status,params long[] ranks)
    {
        await using var db=fixture.CreateContext(host.Clock);var rows=ranks.Select((rank,i)=>new TaskItem{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,Title="PRIVATE_RANK_CANARY "+i,Status=status,BoardRank=rank}).ToArray();db.Tasks.AddRange(rows);await db.SaveChangesAsync();return rows;
    }
    [MySqlFact]
    public async Task BoardMoveCreationAndExistingStatusActionAppendUsingServerRanks()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var path=W(c.A)+"/projects/"+c.A1.Id+"/tasks";
        var first=await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new{title="New first"}));var second=await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new{title="New second"}));
        var one=long.Parse(first.GetProperty("id").GetString()!);var two=long.Parse(second.GetProperty("id").GetString()!);
        await using(var db=fixture.CreateContext()){Assert.Equal(1024,(await db.Tasks.SingleAsync(t=>t.Id==one)).BoardRank);Assert.Equal(2048,(await db.Tasks.SingleAsync(t=>t.Id==two)).BoardRank);}
        await Body(await RevisionWrite(owner,HttpMethod.Patch,W(c.A)+"/tasks/"+two+"/status","1",new{status="Done"}));
        await Body(await RevisionWrite(owner,HttpMethod.Patch,W(c.A)+"/tasks/"+one+"/status","1",new{status="Done"}));
        var column=await Body(await owner.GetAsync(BoardPath(c)+"/columns/Done"));Assert.Equal(new[]{two.ToString(),one.ToString()},column.GetProperty("items").EnumerateArray().Select(t=>t.GetProperty("id").GetString()));
        var list=await Body(await owner.GetAsync(path+"?status=Done&sortBy=rank&sortDirection=asc"));Assert.Equal(column.ToString(),list.ToString());
    }
    [MySqlFact]
    public async Task BoardMoveStatusRankRevisionAuditAndOutboxCommitAsOneFact()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var anchors=await RankCards(host,c,TaskState.Done,1024,2048);
        var result=await Body(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done",anchors[0].Id,"1",anchors[1].Id,"1")));Assert.Equal("2",result.GetProperty("version").GetString());Assert.Equal("Done",result.GetProperty("status").GetString());
        await using var db=fixture.CreateContext();var task=await db.Tasks.SingleAsync(t=>t.Id==c.TaskA.Id);Assert.Equal(1536,task.BoardRank);Assert.Equal(2,task.Version);
        var audit=await db.ActivityLogs.SingleAsync(a=>a.WorkspaceId==c.A.Id&&a.ActionType==ActivityAction.TaskMoved);Assert.Contains("boardRank",audit.ChangesJson);Assert.Contains("status",audit.ChangesJson);Assert.DoesNotContain("PRIVATE_RANK_CANARY",audit.ChangesJson);
        var message=await db.OutboxEvents.SingleAsync(e=>e.WorkspaceId==c.A.Id);Assert.Equal("Project.TaskMoved",message.Kind);Assert.Equal(task.Id,message.TaskId);Assert.Single(await db.OutboxDeliveries.Where(d=>d.EventId==message.Id).ToArrayAsync());
        var board=await Body(await owner.GetAsync(BoardPath(c)));Assert.Equal(3,board.GetProperty("totalCount").GetInt32());Assert.Equal(3,board.GetProperty("columns")[2].GetProperty("totalCount").GetInt32());
        var project=await Body(await owner.GetAsync(W(c.A)+"/projects/"+c.A1.Id));Assert.Equal(100,project.GetProperty("progressPercentage").GetInt32());
    }
    [MySqlFact]
    public async Task BoardMoveReordersWithinColumnAndCurrentPositionIsANoop()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var cards=await RankCards(host,c,TaskState.ToDo,1024,2048);
        var placement=Position("ToDo",c.TaskA.Id,"1",cards[0].Id,"1");var path=MovePath(c,cards[1].Id);
        var moved=await Body(await RevisionWrite(owner,HttpMethod.Post,path,"1",placement));Assert.Equal("2",moved.GetProperty("version").GetString());
        var same=await Body(await RevisionWrite(owner,HttpMethod.Post,path,"2",placement));Assert.Equal(moved.ToString(),same.ToString());
        await Problem(await RevisionWrite(owner,HttpMethod.Post,path,"1",placement),HttpStatusCode.Conflict);
        var rows=await Body(await owner.GetAsync(BoardPath(c)+"/columns/ToDo"));Assert.Equal(new[]{c.TaskA.Id.ToString(),cards[1].Id.ToString(),cards[0].Id.ToString()},rows.GetProperty("items").EnumerateArray().Select(t=>t.GetProperty("id").GetString()));
        await using var db=fixture.CreateContext();Assert.Single(await db.OutboxEvents.Where(e=>e.WorkspaceId==c.A.Id).ToArrayAsync());
    }
    [MySqlFact]
    public async Task BoardMoveConcurrentSameCardHasOneWinnerAndNoLostUpdate()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var a=await host.LoginAsync(0);using var b=await host.LoginAsync(0);
        var results=await Task.WhenAll(RevisionWrite(a,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done")),RevisionWrite(b,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("InProgress")));
        Assert.Single(results,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);
        await using var db=fixture.CreateContext();Assert.Equal(2,(await db.Tasks.SingleAsync(t=>t.Id==c.TaskA.Id)).Version);Assert.Single(await db.OutboxEvents.Where(e=>e.WorkspaceId==c.A.Id).ToArrayAsync());
    }
    [MySqlFact]
    public async Task BoardMoveConcurrentDifferentCardsCannotBothOccupyCapturedGap()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var a=await host.LoginAsync(0);using var b=await host.LoginAsync(0);var anchors=await RankCards(host,c,TaskState.Done,1024,2048);var other=(await RankCards(host,c,TaskState.ToDo,1024))[0];
        var position=Position("Done",anchors[0].Id,"1",anchors[1].Id,"1");var results=await Task.WhenAll(RevisionWrite(a,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",position),RevisionWrite(b,HttpMethod.Post,MovePath(c,other.Id),"1",position));
        Assert.Single(results,r=>r.StatusCode==HttpStatusCode.OK);Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict);await using var db=fixture.CreateContext();Assert.Equal(3,await db.Tasks.CountAsync(t=>t.ProjectId==c.A1.Id&&t.Status==TaskState.Done));
    }
    [MySqlFact]
    public async Task BoardMoveRejectsForeignMissingSelfStaleAndNonadjacentAnchors()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var anchors=await RankCards(host,c,TaskState.Done,1024,2048,3072);var path=MovePath(c,c.TaskA.Id);
        foreach(var id in new[]{c.TaskB.Id,long.MaxValue})await Problem(await RevisionWrite(owner,HttpMethod.Post,path,"1",Position("Done",id,"1")),HttpStatusCode.NotFound);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,W(c.B)+"/projects/"+c.A1.Id+"/tasks/"+c.TaskA.Id+"/move","1",Position("Done")),HttpStatusCode.NotFound);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,W(c.A)+"/projects/"+c.A2.Id+"/tasks/"+c.TaskA.Id+"/move","1",Position("Done")),HttpStatusCode.NotFound);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,path,"1",Position("Done",c.TaskA.Id,"1")),HttpStatusCode.BadRequest);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,path,"1",Position("Done",anchors[0].Id,"1",anchors[0].Id,"1")),HttpStatusCode.BadRequest);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,path,"1",Position("Done",anchors[0].Id,"1",anchors[2].Id,"1")),HttpStatusCode.Conflict);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,path,"1",Position("Done",anchors[2].Id,"2")),HttpStatusCode.Conflict);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,path,"1",Position("Done")),HttpStatusCode.Conflict);
        await using var db=fixture.CreateContext();Assert.False(await db.OutboxEvents.AnyAsync(e=>e.WorkspaceId==c.A.Id));Assert.Equal(1,(await db.Tasks.SingleAsync(t=>t.Id==c.TaskA.Id)).Version);
    }
    [MySqlFact]
    public async Task BoardMoveDenseGapRebalancesWithIndependentRevisionAndMetadataFacts()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var anchors=await RankCards(host,c,TaskState.Done,1,2);
        await GrantNotificationRecipient(host,c);using var recipient=await host.LoginAsync(2);await Watch(host,recipient,c,true,"0");
        foreach(var anchor in anchors)await Body(await host.MemberWriteAsync(recipient,HttpMethod.Put,W(c.A)+"/tasks/"+anchor.Id+"/watcher",new{isWatching=true,expectedVersion="0"}));
        await Body(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done",anchors[0].Id,"1",anchors[1].Id,"1")));
        await using var db=fixture.CreateContext();var rows=await db.Tasks.Where(t=>t.ProjectId==c.A1.Id).OrderBy(t=>t.BoardRank).ToArrayAsync();Assert.Equal(new long[]{1024,2048,3072},rows.Select(t=>t.BoardRank));Assert.All(rows,t=>Assert.Equal(2,t.Version));
        var feed=await Body(await owner.GetAsync(W(c.A)+"/projects/"+c.A1.Id+"/activity"));Assert.Contains(feed.GetProperty("items").EnumerateArray(),a=>a.GetProperty("details").GetString()=="Task moved on the board.");Assert.Contains(feed.GetProperty("items").EnumerateArray(),a=>a.GetProperty("details").GetString()=="Board order spacing updated.");
        Assert.Equal(2,await db.ActivityLogs.CountAsync(a=>a.WorkspaceId==c.A.Id&&a.ActionType==ActivityAction.TaskRankRebalanced));Assert.Equal(3,await db.OutboxEvents.CountAsync(e=>e.WorkspaceId==c.A.Id));
        var inbox=await db.UserNotifications.Where(n=>n.WorkspaceId==c.A.Id&&n.RecipientUserId==host.Users[2].Id).ToArrayAsync();Assert.Single(inbox);Assert.Equal(c.TaskA.Id,inbox[0].TaskId);Assert.Equal("TaskWatched",inbox[0].Kind);
        Assert.All(await db.ActivityLogs.Where(a=>a.WorkspaceId==c.A.Id&&a.ActionType==ActivityAction.TaskRankRebalanced).ToArrayAsync(),a=>{Assert.Contains("boardRank",a.ChangesJson);Assert.DoesNotContain("PRIVATE_RANK_CANARY",a.ChangesJson);});
    }
    [MySqlFact]
    public async Task BoardMoveOutboxFailureRollsBackRebalanceStatusVersionsAndHistory()
    {
        await using var host=new ProjectHost(fixture,true,true,true,new FailOutbox());await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var anchors=await RankCards(host,c,TaskState.Done,1,2);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done",anchors[0].Id,"1",anchors[1].Id,"1")),HttpStatusCode.InternalServerError);
        await using var db=fixture.CreateContext();var rows=await db.Tasks.Where(t=>t.ProjectId==c.A1.Id).OrderBy(t=>t.BoardRank).ToArrayAsync();Assert.Equal(new long[]{0,1,2},rows.Select(t=>t.BoardRank));Assert.All(rows,t=>Assert.Equal(1,t.Version));Assert.Equal(TaskState.ToDo,rows[0].Status);Assert.False(await db.OutboxEvents.AnyAsync(e=>e.WorkspaceId==c.A.Id));Assert.False(await db.ActivityLogs.AnyAsync(a=>a.WorkspaceId==c.A.Id&&a.ActionType==ActivityAction.TaskMoved));
    }
    [MySqlFact]
    public async Task BoardMoveRebalanceAt500CardLimitAndOversizedColumnFailsUnchanged()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var anchors=await RankCards(host,c,TaskState.Done,Enumerable.Range(1,499).Select(i=>(long)i).ToArray());
        await Body(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done",anchors[0].Id,"1",anchors[1].Id,"1")));
        var extra=(await RankCards(host,c,TaskState.ToDo,1024))[0];
        var error=await Problem(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,extra.Id),"1",Position("Done")),HttpStatusCode.Conflict);Assert.Equal("board.capacity_exceeded",error.GetProperty("code").GetString());
        await using var db=fixture.CreateContext();Assert.Equal(500,await db.Tasks.CountAsync(t=>t.ProjectId==c.A1.Id&&t.Status==TaskState.Done));Assert.Equal(1,(await db.Tasks.SingleAsync(t=>t.Id==extra.Id)).Version);Assert.Equal(500,await db.OutboxEvents.CountAsync(e=>e.WorkspaceId==c.A.Id));
    }
    [MySqlFact]
    public async Task BoardMoveRequiresCapturedTaskRevisionCsrfAndEditorAccess()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);using var admin=await host.LoginAsync(2);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),null,Position("Done")),(HttpStatusCode)428);
        await Problem(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"01",Position("Done")),HttpStatusCode.BadRequest);
        await Problem(await owner.PostAsJsonAsync(MovePath(c,c.TaskA.Id),Position("Done")),HttpStatusCode.BadRequest);
        await Problem(await RevisionWrite(admin,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done")),HttpStatusCode.NotFound);
        await GrantNotificationRecipient(host,c);await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Role,MembershipRole.Viewer));
        await Problem(await RevisionWrite(admin,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done")),HttpStatusCode.Forbidden);
    }
    [MySqlFact]
    public async Task BoardMoveStrictPayloadNeverAcceptsRankOrActorAndDuplicateFields()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        var csrf=(await owner.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf")).GetProperty("requestToken").GetString();
        foreach(var json in new[]{"{}","[]","{\"status\":\"done\"}","{\"status\":\"Done\",\"rank\":5}","{\"status\":\"Done\",\"actorId\":\"1\"}","{\"status\":\"Done\",\"status\":\"ToDo\"}","{\"status\":\"Done\",\"previousTaskId\":\"1\"}","{\"status\":\"Done\",\"nextVersion\":1}"})
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,MovePath(c,c.TaskA.Id)){Content=new StringContent(json,Encoding.UTF8,"application/json")};request.Headers.Add("X-CSRF-TOKEN",csrf);request.Headers.Add("X-Expected-Version","1");await Problem(await owner.SendAsync(request),HttpStatusCode.BadRequest);
        }
    }
    private sealed class MoveCompanyWait : DbCommandInterceptor
    {
        public bool Armed {get;set;}
        public TaskCompletionSource Waiting {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken token=default)
        {if(Armed&&command.CommandText.Contains("Workspaces",StringComparison.Ordinal)&&command.CommandText.Contains("FOR UPDATE",StringComparison.Ordinal))Waiting.TrySetResult();return ValueTask.FromResult(result);}
    }
    [MySqlFact]
    public async Task BoardMoveQueuedBehindRevocationRechecksCurrentProjectAuthority()
    {
        var gate=new MoveCompanyWait();await using var host=new ProjectHost(fixture,true,true,true,gate);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var editor=await host.LoginAsync(2);
        await using var mutation=fixture.CreateContext();await using var transaction=await mutation.Database.BeginTransactionAsync();
        await mutation.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={c.A.Id} FOR UPDATE").ToArrayAsync();
        await mutation.ProjectMembers.IgnoreQueryFilters().Where(m=>m.WorkspaceId==c.A.Id&&m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.RevokedAtUtc,DateTime.UtcNow));
        gate.Armed=true;var pending=RevisionWrite(editor,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done"));
        await gate.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));await transaction.CommitAsync();gate.Armed=false;
        await Problem(await pending,HttpStatusCode.NotFound);await using var db=fixture.CreateContext();Assert.Equal(1,(await db.Tasks.SingleAsync(t=>t.Id==c.TaskA.Id)).Version);Assert.False(await db.OutboxEvents.AnyAsync(e=>e.WorkspaceId==c.A.Id));
    }
    [MySqlFact]
    public async Task BoardMoveRepairsExhaustedTailWithoutOverflowAndStatusWriteFailsSafely()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var anchor=(await RankCards(host,c,TaskState.Done,long.MaxValue))[0];
        var problem=await Problem(await RevisionWrite(owner,HttpMethod.Patch,W(c.A)+"/tasks/"+c.TaskA.Id+"/status","1",new{status="Done"}),HttpStatusCode.Conflict);Assert.Equal("board.rank_exhausted",problem.GetProperty("code").GetString());
        await Body(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done",anchor.Id,"1")));
        await using var db=fixture.CreateContext();var rows=await db.Tasks.Where(t=>t.ProjectId==c.A1.Id).OrderBy(t=>t.BoardRank).ToArrayAsync();Assert.Equal(new long[]{1024,2048},rows.Select(t=>t.BoardRank));Assert.All(rows,t=>Assert.Equal(2,t.Version));
    }
    [Fact]
    public void BoardRankDestructiveDownMigrationIsRefused()=>Assert.Throws<InvalidOperationException>(()=>new TaskFlow.Infrastructure.Persistence.Migrations.BoardRank().DownOperations);

    [MySqlFact]
    public async Task BoardMoveNeighborsUseTheSameAssigneeVisibilityAsBoardReads()
    {
        await using var host=new ProjectHost(fixture,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);var hidden=(await RankCards(host,c,TaskState.Done,1024))[0];
        await using(var db=fixture.CreateContext())
        {
            await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Role,MembershipRole.Viewer));
            var row=await db.Tasks.SingleAsync(t=>t.Id==hidden.Id);row.AssigneeId=host.Users[2].Id;await db.SaveChangesAsync();
        }
        var column=await Body(await owner.GetAsync(BoardPath(c)+"/columns/Done"));Assert.Equal(0,column.GetProperty("totalCount").GetInt32());
        await Problem(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done",hidden.Id,"2")),HttpStatusCode.NotFound);
        await Body(await RevisionWrite(owner,HttpMethod.Post,MovePath(c,c.TaskA.Id),"1",Position("Done")));
        var current=await Body(await owner.GetAsync(BoardPath(c)+"/columns/Done"));Assert.Equal(1,current.GetProperty("totalCount").GetInt32());Assert.Equal(c.TaskA.Id.ToString(),current.GetProperty("items")[0].GetProperty("id").GetString());
    }

}
