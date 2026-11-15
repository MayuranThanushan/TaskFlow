using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Notifications;
public sealed record WatcherBatchResult(int Scanned,int Delivered,int Suppressed);
public static class WatcherNotificationDelivery
{
    public static async Task CaptureAsync(TaskFlowDbContext db,OutboxEvent message,long? commentId=null,CancellationToken token=default)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Watcher capture requires the business transaction.");
        if(!db.WatchersEnabled||message.Id!=0||db.Entry(message).State!=EntityState.Added||message.SchemaVersion!=1||message.ProjectId is null||message.TaskId is null
            ||message.Kind is not ("Comment.Created" or "Project.TaskUpdated" or "Project.TaskAssigned" or "Project.TaskUnassigned" or "Project.TaskStatusChanged" or "Project.TaskMoved"))return;
        var watchers=await db.TaskWatchers.AsNoTracking().Where(w=>w.WorkspaceId==message.WorkspaceId&&w.TaskId==message.TaskId&&w.ProjectId==message.ProjectId&&w.IsWatching&&w.UserId!=message.ActorUserId).Take(91).ToArrayAsync(token);
        if(watchers.Length>90)throw new InvalidOperationException("Watcher recipient limit exceeded.");
        var watermark=await db.WorkspaceAuditLogs.Where(a=>a.WorkspaceId==message.WorkspaceId).Select(a=>(long?)a.Id).MaxAsync(token)??0;
        var now=db.UtcNow;
        foreach(var watcher in watchers)
        {
            // Direct mentions and assignments take precedence; never create two
            // deliveries for the same recipient and source event.
            if(db.UserNotifications.Local.Any(n=>ReferenceEquals(n.Event,message)&&n.RecipientUserId==watcher.UserId))continue;
            if(!await WatcherAccess.Tasks(db,new(watcher.UserId,message.WorkspaceId)).AnyAsync(t=>t.Id==message.TaskId,token))continue;
            var preference=await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(p=>p.WorkspaceId==message.WorkspaceId&&p.UserId==watcher.UserId,token)??new();
            if(!preference.InAppEnabled||preference.WatcherDeliveryMode=="Off")continue;
            var ready=WatcherTiming.ReadyAt(now,preference);
            var intent=new WatcherNotificationIntent{WorkspaceId=message.WorkspaceId,ProjectId=message.ProjectId.Value,TaskId=message.TaskId.Value,RecipientUserId=watcher.UserId,
                SubscriptionVersion=watcher.Version,AuthorityWatermark=watermark,Event=message,CommentId=commentId,CreatedAtUtc=now,AvailableAtUtc=ready};
            db.WatcherNotificationIntents.Add(intent);
            if(ready<=now)Deliver(db,intent,now);
        }
    }
    private static void Deliver(TaskFlowDbContext db,WatcherNotificationIntent intent,DateTime now)
    {
        db.UserNotifications.Add(new(){WorkspaceId=intent.WorkspaceId,ProjectId=intent.ProjectId,TaskId=intent.TaskId,RecipientUserId=intent.RecipientUserId,
            Kind="TaskWatched",CommentId=intent.CommentId,Event=intent.Event,EventId=intent.EventId,CreatedAtUtc=now});
        intent.State="Delivered";intent.Outcome="InAppDelivered";intent.CompletedAtUtc=now;
    }
    public static async Task<WatcherBatchResult> FlushAsync(TaskFlowDbContext db,long workspace,CancellationToken token=default)
    {
        if(db.Database.CurrentTransaction is null)throw new InvalidOperationException("Watcher delivery requires the company transaction.");
        if(!db.WatchersEnabled)return new(0,0,0);
        var now=db.UtcNow;
        var intents=await db.WatcherNotificationIntents.Where(i=>i.WorkspaceId==workspace&&i.State=="Pending"&&i.AvailableAtUtc<=now).OrderBy(i=>i.AvailableAtUtc).ThenBy(i=>i.Id).Take(100).ToArrayAsync(token);
        var delivered=0;var suppressed=0;
        foreach(var intent in intents)
        {
            string? reason=null;
            var subscription=await db.TaskWatchers.AsNoTracking().SingleOrDefaultAsync(w=>w.WorkspaceId==workspace&&w.TaskId==intent.TaskId&&w.UserId==intent.RecipientUserId,token);
            if(intent.CreatedAtUtc<=now.AddDays(-7))reason="Expired";
            else if(subscription is null||!subscription.IsWatching||subscription.Version!=intent.SubscriptionVersion)reason="Unwatched";
            else if(!await WatcherAccess.Tasks(db,new(intent.RecipientUserId,workspace)).AnyAsync(t=>t.Id==intent.TaskId&&t.ProjectId==intent.ProjectId,token))reason="Unavailable";
            else if(intent.CommentId is {} comment&&!await db.TaskComments.AnyAsync(c=>c.WorkspaceId==workspace&&c.Id==comment&&c.TaskId==intent.TaskId&&c.DeletedAtUtc==null,token))reason="Unavailable";
            // An intervening revoke/suspend remains a fence even if access was
            // restored before this batch. IDs avoid timestamp-resolution races.
            else if(await db.WorkspaceAuditLogs.AnyAsync(a=>a.WorkspaceId==workspace&&a.Id>intent.AuthorityWatermark
                &&((a.TargetUserId==intent.RecipientUserId&&(a.Action=="CompanyMemberSuspended"||a.Action=="CompanyMemberRemoved"||a.Action=="CompanyMemberLeft"||(a.Action=="ProjectMemberRemoved"&&a.ProjectId==intent.ProjectId)))
                    ||(a.Action=="ProjectOwnershipTransferred"&&a.ActorUserId==intent.RecipientUserId&&a.ProjectId==intent.ProjectId)),token))reason="Revoked";
            var preference=await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(p=>p.WorkspaceId==workspace&&p.UserId==intent.RecipientUserId,token)??new();
            if(reason is null&&(!preference.InAppEnabled||preference.WatcherDeliveryMode=="Off"))reason="Muted";
            if(reason is not null){intent.State="Suppressed";intent.Outcome=reason;intent.CompletedAtUtc=now;suppressed++;continue;}
            var ready=WatcherTiming.ReadyAt(now,preference);
            if(ready>now){intent.AvailableAtUtc=ready;continue;}
            // The recipient/event unique key protects retries. Both marker and
            // inbox row commit together; no external side effect needs a lease.
            if(!await db.UserNotifications.AnyAsync(n=>n.WorkspaceId==workspace&&n.RecipientUserId==intent.RecipientUserId&&n.EventId==intent.EventId,token))
            {
                intent.Event=await db.OutboxEvents.SingleAsync(e=>e.WorkspaceId==workspace&&e.Id==intent.EventId,token);Deliver(db,intent,now);delivered++;
            }
            else{intent.State="Delivered";intent.Outcome="InAppDelivered";intent.CompletedAtUtc=now;}
        }
        return new(intents.Length,delivered,suppressed);
    }
}
