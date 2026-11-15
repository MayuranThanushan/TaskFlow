using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Notifications;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private static string WatchPath(Companies c)=>W(c.A)+"/tasks/"+c.TaskA.Id+"/watcher";
    private async Task Watch(ProjectHost h,HttpClient client,Companies c,bool enabled,string version)=>_ = await Body(await h.MemberWriteAsync(client,HttpMethod.Put,WatchPath(c),new{isWatching=enabled,expectedVersion=version}));
    private async Task<NotificationBatchResult> FlushWatchers(long workspace,DateTimeOffset at)
    {
        var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Notifications:Enabled"]="true",["Notifications:WatchersEnabled"]="true",["Notifications:SchedulingEnabled"]="true",["WorkspaceReads:Enabled"]="true",["WorkspaceWrites:Enabled"]="true",["WorkspaceLifecycle:Enabled"]="true"}).Build();
        var clock=new FixedClock{Now=at};await using var db=new TaskFlowDbContext(new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).Options,clock,config);
        return await new NotificationScheduler(db,clock,config).RunAsync(workspace);
    }
    private Task<HttpResponseMessage> WatchNoise(ProjectHost host,HttpClient client,Companies c,string mode,int? start,int? end,string version="0",bool daily=false)=>host.MemberWriteAsync(client,HttpMethod.Put,W(c.A)+"/notifications/preferences",new{inAppEnabled=true,taskAssignedEnabled=true,dueReminderEnabled=false,dailyDigestEnabled=daily,timeZoneId="UTC",digestHour=10,watcherDeliveryMode=mode,quietStartHour=start,quietEndHour=end,expectedVersion=version});
    [MySqlFact]
    public async Task WatchersAreSelfOnlyVersionedAndPersonalSubscriptionsAllowReadOnlyCollaborators()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);
        using var recipient=await host.LoginAsync(2);using var second=await host.LoginAsync(2);using var owner=await host.LoginAsync(0);using var outsider=await host.LoginAsync(1);var path=WatchPath(c);
        var initial=await Body(await recipient.GetAsync(path));Assert.False(initial.GetProperty("isWatching").GetBoolean());Assert.Equal("0",initial.GetProperty("version").GetString());Assert.Equal(2,initial.EnumerateObject().Count());
        var writes=await Task.WhenAll(host.MemberWriteAsync(recipient,HttpMethod.Put,path,new{isWatching=true,expectedVersion="0"}),host.MemberWriteAsync(second,HttpMethod.Put,path,new{isWatching=true,expectedVersion="0"}));Assert.Single(writes,r=>r.IsSuccessStatusCode);Assert.Single(writes,r=>r.StatusCode==HttpStatusCode.Conflict);
        await Watch(host,recipient,c,true,"1");Assert.Equal("1",(await Body(await recipient.GetAsync(path))).GetProperty("version").GetString());
        Assert.False((await Body(await owner.GetAsync(path))).GetProperty("isWatching").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(recipient,HttpMethod.Put,path,new{isWatching=false,expectedVersion="1",userId=host.Users[0].Id.ToString()})).StatusCode);
        await Problem(await outsider.GetAsync(path+"?userId=1"),HttpStatusCode.NotFound);await Problem(await owner.GetAsync(W(c.B)+"/tasks/"+c.TaskA.Id+"/watcher"),HttpStatusCode.NotFound);
        await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Role,MembershipRole.Viewer));
        await Watch(host,recipient,c,false,"1");await Watch(host,recipient,c,true,"2");
        var guest=W(c.B)+"/tasks/"+c.TaskB.Id+"/watcher";Assert.True((await Body(await host.MemberWriteAsync(owner,HttpMethod.Put,guest,new{isWatching=true,expectedVersion="0"}))).GetProperty("isWatching").GetBoolean());
    }
    [MySqlFact]
    public async Task WatchersDeliverFutureChangesOnceAndMentionsTakePrecedenceWithoutSelfNoise()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);var comments=CommentsPath(c);
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,comments,new{body="Before subscription"}));await Watch(host,recipient,c,true,"0");
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,comments,new{body="Private watched canary"}));await Body(await host.MemberWriteAsync(recipient,HttpMethod.Post,comments,new{body="Self action"}));
        var mention=await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,comments,new{body="Priority @["+host.Users[2].Id+"]"}));
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Put,comments+"/"+mention.GetProperty("id").GetString(),new{body="Edited @["+host.Users[2].Id+"]",expectedVersion="1"}));
        var notifications=await Body(await recipient.GetAsync(W(c.A)+"/notifications"));Assert.Equal(2,notifications.GetProperty("totalCount").GetInt32());Assert.Contains(notifications.GetProperty("items").EnumerateArray(),n=>n.GetProperty("kind").GetString()=="TaskWatched");
        await Watch(host,recipient,c,false,"1");await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,comments,new{body="After unsubscribe"}));Assert.Equal(2,(await Body(await recipient.GetAsync(W(c.A)+"/notifications"))).GetProperty("totalCount").GetInt32());
        await Watch(host,recipient,c,true,"2");await Body(await host.MemberWriteAsync(owner,HttpMethod.Patch,W(c.A)+"/tasks/"+c.TaskA.Id+"/status",new{status="InProgress"}));Assert.Equal(3,(await Body(await recipient.GetAsync(W(c.A)+"/notifications"))).GetProperty("totalCount").GetInt32());
        await using var db=fixture.CreateContext();Assert.DoesNotContain("canary",JsonSerializer.Serialize(await db.WatcherNotificationIntents.Where(i=>i.WorkspaceId==c.A.Id).ToArrayAsync()),StringComparison.OrdinalIgnoreCase);
        await using var tx=await db.Database.BeginTransactionAsync();var historical=await db.OutboxEvents.FirstAsync(e=>e.WorkspaceId==c.A.Id&&e.Kind=="Comment.Created");await WatcherNotificationDelivery.CaptureAsync(db,historical);Assert.DoesNotContain(db.ChangeTracker.Entries<WatcherNotificationIntent>(),e=>e.State==EntityState.Added);
    }
    [MySqlFact]
    public async Task WatcherDeferredDeliveryHonorsUnsubscribeAndInterveningRevocationEvenAfterRestore()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);
        await Watch(host,recipient,c,true,"0");await Body(await WatchNoise(host,recipient,c,"Immediate",8,10));
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Pending old subscription"}));Assert.Equal(0,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(1))).WatcherScanned);
        await Watch(host,recipient,c,false,"1");await Watch(host,recipient,c,true,"2");Assert.Equal(1,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2))).WatcherSuppressed);
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Pending before suspension"}));
        var state=W(c.A)+"/members/"+host.Users[2].Id+"/state";Assert.Equal(HttpStatusCode.NoContent,(await host.MemberWriteAsync(owner,HttpMethod.Patch,state,new{state="Suspended"})).StatusCode);Assert.Equal(HttpStatusCode.NoContent,(await host.MemberWriteAsync(owner,HttpMethod.Patch,state,new{state="Active"})).StatusCode);
        Assert.Equal(1,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2))).WatcherSuppressed);
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="New authorized event"}));var batches=await Task.WhenAll(FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2)),FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2)));Assert.Equal(1,batches.Sum(b=>b.WatcherDelivered));
        await using var db=fixture.CreateContext();var rows=await db.WatcherNotificationIntents.Where(i=>i.WorkspaceId==c.A.Id).ToArrayAsync();Assert.Contains(rows,i=>i.Outcome=="Revoked");Assert.Contains(rows,i=>i.Outcome=="Unwatched");Assert.Single(rows,i=>i.State=="Delivered");
    }
    [MySqlFact]
    public async Task WatcherDigestMuteDeletionAndExpiryDoNotLeakStaleContent()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);
        await Watch(host,recipient,c,true,"0");await Body(await WatchNoise(host,recipient,c,"Digest",null,null,daily:true));
        var comment=await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Digest work"}));Assert.Equal(0,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(1))).WatcherDelivered);Assert.Equal(1,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2))).WatcherDelivered);Assert.Equal(0,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2))).WatcherDelivered);
        var pending=await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Deleted before digest"}));Assert.Equal(HttpStatusCode.NoContent,(await host.MemberWriteAsync(owner,HttpMethod.Delete,CommentsPath(c)+"/"+pending.GetProperty("id").GetString(),new{expectedVersion="1"})).StatusCode);Assert.Equal(1,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2))).WatcherSuppressed);
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Muted before digest"}));await Body(await WatchNoise(host,recipient,c,"Off",null,null,"1",true));Assert.Equal(0,(await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2))).WatcherScanned);
        await Body(await WatchNoise(host,recipient,c,"Digest",null,null,"2",true));await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Expired digest"}));Assert.Equal(1,(await FlushWatchers(c.A.Id,host.Clock.Now.AddDays(8).AddHours(2))).WatcherSuppressed);
        await using var db=fixture.CreateContext();var outcomes=await db.WatcherNotificationIntents.Where(i=>i.WorkspaceId==c.A.Id).Select(i=>i.Outcome).ToArrayAsync();Assert.Contains("Unavailable",outcomes);Assert.Contains("Muted",outcomes);Assert.Contains("Expired",outcomes);
    }
    [MySqlFact]
    public async Task WatcherGroupedInboxRetainsSourceRowsAndRechecksRevocationAndReadState()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);await Watch(host,recipient,c,true,"0");
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="First source"}));await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Second source"}));
        var path=W(c.A)+"/notifications";var groups=await Body(await recipient.GetAsync(path+"/groups"));var group=Assert.Single(groups.GetProperty("items").EnumerateArray());Assert.Equal(2,group.GetProperty("notificationCount").GetInt32());Assert.Equal(2,group.GetProperty("unreadCount").GetInt32());
        var rows=await Body(await recipient.GetAsync(path+"?taskId="+c.TaskA.Id));var id=rows.GetProperty("items")[0].GetProperty("id").GetString();Assert.Equal(2,rows.GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent,(await host.MemberWriteAsync(recipient,HttpMethod.Patch,path+"/"+id,new{isRead=true})).StatusCode);Assert.Equal(1,(await Body(await recipient.GetAsync(path+"/groups"))).GetProperty("items")[0].GetProperty("unreadCount").GetInt32());
        Assert.Equal(0,(await Body(await owner.GetAsync(path+"/groups"))).GetProperty("totalCount").GetInt32());Assert.Equal(0,(await Body(await recipient.GetAsync(path+"?taskId="+c.TaskB.Id))).GetProperty("totalCount").GetInt32());
        await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.RevokedAtUtc,DateTime.UtcNow));
        Assert.Equal(0,(await Body(await recipient.GetAsync(path+"/groups"))).GetProperty("totalCount").GetInt32());await Problem(await recipient.GetAsync(WatchPath(c)),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task WatcherNoiseValidationDefaultsAndDisabledRolloutCannotChangeOtherSettings()
    {
        await using(var disabled=new ProjectHost(fixture,true,true,true,false,true,true)){await disabled.InitializeAsync();var c=await SeedCompanies(disabled);using var owner=await disabled.LoginAsync(0);await Problem(await owner.GetAsync(WatchPath(c)),HttpStatusCode.NotFound);}
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var company=await SeedCompanies(host);using var user=await host.LoginAsync(0);var path=W(company.A)+"/notifications/preferences";
        foreach(var pair in new[]{(8,8),(-1,9),(22,24)})Assert.Equal(HttpStatusCode.BadRequest,(await WatchNoise(host,user,company,"Immediate",pair.Item1,pair.Item2)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await WatchNoise(host,user,company,"Digest",null,null)).StatusCode);
        var saved=await Body(await WatchNoise(host,user,company,"Digest",22,7,daily:true));Assert.Equal("Digest",saved.GetProperty("watcherDeliveryMode").GetString());
        var old=await Body(await host.MemberWriteAsync(user,HttpMethod.Put,path,new{inAppEnabled=false,taskAssignedEnabled=true,expectedVersion="1"}));Assert.Equal("Digest",old.GetProperty("watcherDeliveryMode").GetString());Assert.Equal(22,old.GetProperty("quietStartHour").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(user,HttpMethod.Put,path,new{inAppEnabled=true,taskAssignedEnabled=true,quietStartHour=8,quietEndHour=10,expectedVersion="2"})).StatusCode);
    }
    [MySqlFact]
    public async Task WatcherFanoutIsBoundedAndDirectMentionsDeduplicateWatcherRecipients()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var users=new List<ApplicationUser>();
        await using(var db=fixture.CreateContext())
        {
            for(var n=0;n<90;n++){var email=Guid.NewGuid().ToString("N")+"@example.test";var u=new ApplicationUser{DisplayName="Synthetic watcher",Email=email,NormalizedEmail=email.ToUpperInvariant(),UserName=email,NormalizedUserName=email.ToUpperInvariant()};users.Add(u);db.Users.Add(u);}await db.SaveChangesAsync();host.Users.AddRange(users);
            db.WorkspaceMemberships.AddRange(users.Select(u=>new WorkspaceMembership{WorkspaceId=c.A.Id,UserId=u.Id,Role=WorkspaceRole.Member}));await db.SaveChangesAsync();
            db.ProjectMembers.AddRange(users.Select(u=>new ProjectMember{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,UserId=u.Id,Role=MembershipRole.Viewer}));await db.SaveChangesAsync();
            db.TaskWatchers.AddRange(users.Select(u=>new TaskWatcher{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,TaskId=c.TaskA.Id,UserId=u.Id,IsWatching=true}));await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(owner,HttpMethod.Put,WatchPath(c),new{isWatching=true,expectedVersion="0"})).StatusCode);
        var text=string.Join(' ',users.Take(10).Select(u=>"@["+u.Id+"]"));await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body=text}));
        await using var check=fixture.CreateContext();var rows=await check.UserNotifications.Where(n=>n.WorkspaceId==c.A.Id).ToArrayAsync();Assert.Equal(90,rows.Length);Assert.Equal(10,rows.Count(n=>n.Kind=="TaskMention"));Assert.Equal(80,rows.Count(n=>n.Kind=="TaskWatched"));Assert.Equal(90,rows.Select(n=>n.RecipientUserId).Distinct().Count());
    }
    [MySqlFact]
    public async Task WatcherSourceFailureRollsBackCommentIntentAndInboxTogether()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true,new FailOutbox());await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);await Watch(host,recipient,c,true,"0");
        await Problem(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Atomic watcher rollback"}),HttpStatusCode.InternalServerError);
        await using var db=fixture.CreateContext();Assert.False(await db.TaskComments.AnyAsync(t=>t.WorkspaceId==c.A.Id));Assert.False(await db.WatcherNotificationIntents.AnyAsync(i=>i.WorkspaceId==c.A.Id));Assert.False(await db.UserNotifications.AnyAsync(n=>n.WorkspaceId==c.A.Id));
    }
    [MySqlFact]
    public async Task WatcherSchedulerRollbackPreservesPendingIntentAndRetryDeliversOnce()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);await Watch(host,recipient,c,true,"0");await Body(await WatchNoise(host,recipient,c,"Immediate",8,10));await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Pending retry"}));
        var configuration=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{["Notifications:Enabled"]="true",["Notifications:WatchersEnabled"]="true",["Notifications:SchedulingEnabled"]="true",["WorkspaceReads:Enabled"]="true",["WorkspaceWrites:Enabled"]="true",["WorkspaceLifecycle:Enabled"]="true"}).Build();
        var clock=new FixedClock{Now=host.Clock.Now.AddHours(2)};var options=new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(new FailScheduledCommit()).Options;
        await using(var failed=new TaskFlowDbContext(options,clock,configuration))await Assert.ThrowsAsync<InvalidOperationException>(()=>new NotificationScheduler(failed,clock,configuration).RunAsync(c.A.Id));
        await using(var db=fixture.CreateContext()){Assert.Equal("Pending",(await db.WatcherNotificationIntents.SingleAsync(i=>i.WorkspaceId==c.A.Id)).State);Assert.False(await db.UserNotifications.AnyAsync(n=>n.WorkspaceId==c.A.Id));}
        Assert.Equal(1,(await FlushWatchers(c.A.Id,clock.Now)).WatcherDelivered);Assert.Equal(0,(await FlushWatchers(c.A.Id,clock.Now)).WatcherDelivered);
    }

    [MySqlFact]
    public async Task WatcherTimingPreferenceChangeReleasesPendingWorkAtTheNewEligibleTime()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);
        await Watch(host,recipient,c,true,"0");await Body(await WatchNoise(host,recipient,c,"Immediate",8,10));await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="Rescheduled personal timing"}));
        Assert.Equal(0,(await FlushWatchers(c.A.Id,host.Clock.Now)).WatcherScanned);await Body(await WatchNoise(host,recipient,c,"Immediate",null,null,"1"));
        Assert.Equal(1,(await FlushWatchers(c.A.Id,host.Clock.Now)).WatcherDelivered);Assert.Equal(0,(await FlushWatchers(c.A.Id,host.Clock.Now)).WatcherDelivered);
    }

}
