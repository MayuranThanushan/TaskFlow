using System.Data.Common;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Email;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Email;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Outbox;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private sealed class PauseAfterClaim : DbTransactionInterceptor
    {
        public TaskCompletionSource Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int commits;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            if (Interlocked.Increment(ref commits) == 1) { Claimed.TrySetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(15), token); }
        }
    }
    [MySqlFact]
    public async Task DeliveryReliabilityRevocationAfterClaimBeforeSendNeverCallsProvider()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync();
        var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = long.Parse(invite.GetProperty("id").GetString()!);
        var pause = new PauseAfterClaim(); var sender = new OutageSender { Recover = true };
        await using var db = new TaskFlowDbContext(new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(pause).Options, TimeProvider.System);
        var run = new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender).RunAsync();
        try
        {
            await pause.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A)+"/invitations/"+id+"/revoke", new { expectedVersion="1" })).StatusCode);
        }
        finally { pause.Resume.TrySetResult(); }
        await run; Assert.Empty(sender.MessageIds);
        db.ChangeTracker.Clear(); var row = await db.WorkspaceInvitations.SingleAsync(i=>i.Id==id);
        Assert.Equal("Cancelled",row.DeliveryState); Assert.Null(row.ProtectedMessage); Assert.Null(row.LeaseToken);
    }
    [MySqlFact]
    public async Task DeliveryReliabilityInviterSuspensionInClaimGapSuppressesPrivatePayload()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync();
        var c=await SeedCompanies(host);await VerifyInviteUsers(host);using var owner=await host.LoginAsync(0);
        var invite=await Invite(host,owner,c,host.Users[1].Email!);var id=long.Parse(invite.GetProperty("id").GetString()!);
        var pause=new PauseAfterClaim();var sender=new OutageSender{Recover=true};
        await using var db=new TaskFlowDbContext(new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(pause).Options,TimeProvider.System);
        var run=new InvitationMailCapture(db,host.Services.GetRequiredService<IDataProtectionProvider>(),sender).RunAsync();
        try
        {
            await pause.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var mutation=fixture.CreateContext();await using var tx=await mutation.Database.BeginTransactionAsync();
            await mutation.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={c.A.Id} FOR UPDATE").ToArrayAsync();
            await mutation.WorkspaceMemberships.Where(m=>m.WorkspaceId==c.A.Id&&m.UserId==host.Users[0].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.State,WorkspaceMembershipState.Suspended));
            await tx.CommitAsync();
        }
        finally { pause.Resume.TrySetResult(); }
        await run;Assert.Empty(sender.MessageIds);db.ChangeTracker.Clear();
        var row=await db.WorkspaceInvitations.SingleAsync(i=>i.Id==id);Assert.Equal("Cancelled",row.DeliveryState);Assert.Equal("invitation_mail.unavailable",row.LastCode);Assert.Null(row.ProtectedMessage);
    }
    [MySqlFact]
    public async Task DeliveryReliabilityCancelledWorkerRecoversExpiredLeaseWithStableMessageId()
    {
        await using var host=new ProjectHost(fixture,true,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await VerifyInviteUsers(host);using var owner=await host.LoginAsync(0);
        var invite=await Invite(host,owner,c,host.Users[1].Email!);var id=long.Parse(invite.GetProperty("id").GetString()!);var original=await InviteMail(host,id.ToString());var blocked=new InviteSender();using var stop=new CancellationTokenSource();
        await using(var db=fixture.CreateContext())
        {
            var run=new InvitationMailCapture(db,host.Services.GetRequiredService<IDataProtectionProvider>(),blocked).RunAsync(stop.Token);
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));stop.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>run);
        }
        await using(var db=fixture.CreateContext())
        {
            var row=await db.WorkspaceInvitations.SingleAsync(i=>i.Id==id);Assert.Equal("Leased",row.DeliveryState);Assert.NotNull(row.ProtectedMessage);
            await db.WorkspaceInvitations.Where(i=>i.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.LeaseExpiresAtUtc,DateTime.UtcNow.AddMinutes(-1)));
        }
        var sender=new OutageSender{Recover=true,SuccessStatus=EmailStatus.ProviderAccepted};
        await using(var db=fixture.CreateContext())
        {
            var worker=new InvitationMailCapture(db,host.Services.GetRequiredService<IDataProtectionProvider>(),sender);Assert.Equal(1,await worker.RunAsync());Assert.Equal(0,await worker.RunAsync());
            db.ChangeTracker.Clear();var row=await db.WorkspaceInvitations.SingleAsync(i=>i.Id==id);Assert.Equal(2,row.Attempts);Assert.Equal("ProviderAccepted",row.DeliveryState);Assert.Null(row.ProtectedMessage);
        }
        Assert.Equal(original.MessageId,Assert.Single(sender.MessageIds));
    }
    private sealed class FailAcceptedMailCommit : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token=default)
        {
            if(data.Context!.ChangeTracker.Entries<InvitationTransition>().Any(e=>e.State==EntityState.Added&&e.Entity.Action=="ProviderAccepted"))throw new IOException("PRIVATE_PROVIDER_CANARY");
            return ValueTask.FromResult(result);
        }
    }
    [MySqlFact]
    public async Task DeliveryReliabilityAcceptedButUncommittedSendIsAtLeastOnceWithStableIdentity()
    {
        await using var host=new ProjectHost(fixture,true,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await VerifyInviteUsers(host);using var owner=await host.LoginAsync(0);
        var invite=await Invite(host,owner,c,host.Users[1].Email!);var id=long.Parse(invite.GetProperty("id").GetString()!);var sender=new OutageSender{Recover=true,SuccessStatus=EmailStatus.ProviderAccepted};
        await using(var failed=new TaskFlowDbContext(new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(fixture.ConnectionString).AddInterceptors(new FailAcceptedMailCommit()).Options,TimeProvider.System))
            await Assert.ThrowsAsync<IOException>(()=>new InvitationMailCapture(failed,host.Services.GetRequiredService<IDataProtectionProvider>(),sender).RunAsync());
        await using(var db=fixture.CreateContext())
        {
            Assert.Equal("Leased",(await db.WorkspaceInvitations.SingleAsync(i=>i.Id==id)).DeliveryState);
            Assert.False(await db.InvitationTransitions.AnyAsync(t=>t.InvitationId==id&&t.Action=="ProviderAccepted"));
            await db.WorkspaceInvitations.Where(i=>i.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.LeaseExpiresAtUtc,DateTime.UtcNow.AddMinutes(-1)));
            await new InvitationMailCapture(db,host.Services.GetRequiredService<IDataProtectionProvider>(),sender).RunAsync();
            Assert.Single(await db.InvitationTransitions.Where(t=>t.InvitationId==id&&t.Action=="ProviderAccepted").ToArrayAsync());
        }
        Assert.Equal(2,sender.MessageIds.Count);Assert.Single(sender.MessageIds.Distinct());
    }
    [MySqlFact]
    public async Task DeliveryReliabilityWatcherBacklogDrainsInBoundedBatchesWithoutCrossCompanyDelivery()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);
        var users=new List<ApplicationUser>();
        await using(var db=fixture.CreateContext())
        {
            // 22 recipients plus existing members remain inside the 25-active-member pilot.
            for(var i=0;i<22;i++){var email=Guid.NewGuid().ToString("N")+"@example.test";var u=new ApplicationUser{DisplayName="Synthetic load",Email=email,NormalizedEmail=email.ToUpperInvariant(),UserName=email,NormalizedUserName=email.ToUpperInvariant()};users.Add(u);db.Users.Add(u);}await db.SaveChangesAsync();host.Users.AddRange(users);
            db.WorkspaceMemberships.AddRange(users.Select(u=>new WorkspaceMembership{WorkspaceId=c.A.Id,UserId=u.Id,Role=WorkspaceRole.Member}));await db.SaveChangesAsync();
            db.ProjectMembers.AddRange(users.Select(u=>new ProjectMember{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,UserId=u.Id,Role=MembershipRole.Viewer}));
            db.TaskWatchers.AddRange(users.Select(u=>new TaskWatcher{WorkspaceId=c.A.Id,ProjectId=c.A1.Id,TaskId=c.TaskA.Id,UserId=u.Id,IsWatching=true}));
            db.NotificationPreferences.AddRange(users.Select(u=>new NotificationPreference{WorkspaceId=c.A.Id,UserId=u.Id,QuietStartHour=8,QuietEndHour=10}));await db.SaveChangesAsync();
        }
        var timer=System.Diagnostics.Stopwatch.StartNew();
        for(var i=0;i<10;i++)await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="PRIVATE_LOAD_CANARY "+i}));
        await using(var db=fixture.CreateContext()){Assert.Equal(220,await db.WatcherNotificationIntents.CountAsync(i=>i.WorkspaceId==c.A.Id&&i.State=="Pending"));Assert.False(await db.UserNotifications.AnyAsync(n=>n.WorkspaceId==c.A.Id));}
        var at=host.Clock.Now.AddHours(2);var batches=new List<TaskFlow.Infrastructure.Notifications.NotificationBatchResult>();
        for(var i=0;i<3;i++){var pair=await Task.WhenAll(FlushWatchers(c.A.Id,at),FlushWatchers(c.A.Id,at));batches.AddRange(pair);}
        Assert.All(batches,b=>Assert.InRange(b.WatcherScanned,0,100));Assert.Equal(220,batches.Sum(b=>b.WatcherDelivered));Assert.Equal(0,(await FlushWatchers(c.A.Id,at)).WatcherDelivered);
        await using(var db=fixture.CreateContext())
        {
            var rows=await db.UserNotifications.Where(n=>n.WorkspaceId==c.A.Id).ToArrayAsync();Assert.Equal(220,rows.Length);Assert.Equal(220,rows.Select(n=>(n.RecipientUserId,n.EventId)).Distinct().Count());
            Assert.False(await db.UserNotifications.AnyAsync(n=>n.WorkspaceId==c.B.Id));Assert.DoesNotContain("PRIVATE_LOAD_CANARY",JsonSerializer.Serialize(rows));
        }
        timer.Stop();Assert.True(timer.Elapsed<TimeSpan.FromMinutes(2),"Bounded local pilot load exceeded its two-minute test budget; inspect database/query capacity.");
    }
    [MySqlFact]
    public async Task DeliveryReliabilitySnapshotDistinguishesDueWorkFromFutureQuietHoursWithoutPayloads()
    {
        await using var host=new ProjectHost(fixture,true,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await VerifyInviteUsers(host);using var owner=await host.LoginAsync(0);
        var invite=await Invite(host,owner,c,host.Users[1].Email!);var id=long.Parse(invite.GetProperty("id").GetString()!);
        await using var db=fixture.CreateContext();await db.WorkspaceInvitations.Where(i=>i.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.AvailableAtUtc,DateTime.UtcNow.AddHours(1)));
        var future=await DeliveryHealth.ReadAsync(db);var row=Assert.Single(future.Queues,q=>q.Queue=="invitation_mail");Assert.Equal(1,row.Pending);Assert.Equal(0,row.Due);Assert.Equal(0,row.OldestDueAgeSeconds);
        await db.WorkspaceInvitations.Where(i=>i.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.AvailableAtUtc,DateTime.UtcNow.AddMinutes(-10)));
        var due=await DeliveryHealth.ReadAsync(db);row=Assert.Single(due.Queues,q=>q.Queue=="invitation_mail");Assert.Equal(1,row.Due);Assert.True(row.OldestDueAgeSeconds>=599);
        var json=JsonSerializer.Serialize(due);Assert.DoesNotContain(host.Users[1].Email!,json);Assert.DoesNotContain("Protected",json);Assert.Equal(4,due.Queues.Length);
    }
    [MySqlFact]
    public async Task DeliveryReliabilitySchedulerSkipsCompanyRevocationAndSuppressesAfterCommit()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);
        await Watch(host,recipient,c,true,"0");await Body(await WatchNoise(host,recipient,c,"Immediate",8,10));await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,CommentsPath(c),new{body="PRIVATE_REVOKE_RACE_CANARY"}));
        await using(var mutation=fixture.CreateContext())
        {
            await using var tx=await mutation.Database.BeginTransactionAsync();
            await mutation.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={c.A.Id} FOR UPDATE").ToArrayAsync();
            await mutation.ProjectMembers.IgnoreQueryFilters().Where(m=>m.WorkspaceId==c.A.Id&&m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.RevokedAtUtc,DateTime.UtcNow));
            var blocked=await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2));Assert.True(blocked.Busy);Assert.Equal(0,blocked.WatcherScanned);
            await tx.CommitAsync();
        }
        var result=await FlushWatchers(c.A.Id,host.Clock.Now.AddHours(2));Assert.Equal(1,result.WatcherSuppressed);Assert.Equal(0,result.WatcherDelivered);
        await using var db=fixture.CreateContext();Assert.False(await db.UserNotifications.AnyAsync(n=>n.WorkspaceId==c.A.Id));
    }
    private sealed class RateLimitedSender : IEmailSender
    {
        public int Calls {get;private set;}
        public Task<EmailResult> SendAsync(EmailRequest request,CancellationToken token=default)
        { Calls++;return Task.FromResult(new EmailResult(EmailStatus.RateLimited,"email.provider_rate_limited",DateTimeOffset.UtcNow.AddMinutes(20))); }
    }
    [MySqlFact]
    public async Task DeliveryReliabilityProviderRetryAfterPreventsHotLoopAndReplayAfterRevocation()
    {
        await using var host=new ProjectHost(fixture,true,true,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await VerifyInviteUsers(host);using var owner=await host.LoginAsync(0);
        var invite=await Invite(host,owner,c,host.Users[1].Email!);var id=long.Parse(invite.GetProperty("id").GetString()!);var sender=new RateLimitedSender();
        await using(var db=fixture.CreateContext())
        {
            var worker=new InvitationMailCapture(db,host.Services.GetRequiredService<IDataProtectionProvider>(),sender);Assert.Equal(1,await worker.RunAsync());Assert.Equal(0,await worker.RunAsync());
            db.ChangeTracker.Clear();var row=await db.WorkspaceInvitations.SingleAsync(i=>i.Id==id);Assert.Equal("Pending",row.DeliveryState);Assert.Equal("email.provider_rate_limited",row.LastCode);Assert.True(row.AvailableAtUtc>DateTime.UtcNow.AddMinutes(19));
        }
        Assert.Equal(HttpStatusCode.NoContent,(await host.MemberWriteAsync(owner,HttpMethod.Post,W(c.A)+"/invitations/"+id+"/revoke",new{expectedVersion="1"})).StatusCode);
        await using(var db=fixture.CreateContext()){await db.WorkspaceInvitations.Where(i=>i.Id==id).ExecuteUpdateAsync(s=>s.SetProperty(i=>i.AvailableAtUtc,DateTime.UtcNow.AddMinutes(-1)));Assert.Equal(0,await new InvitationMailCapture(db,host.Services.GetRequiredService<IDataProtectionProvider>(),sender).RunAsync());}
        Assert.Equal(1,sender.Calls);
    }

}
