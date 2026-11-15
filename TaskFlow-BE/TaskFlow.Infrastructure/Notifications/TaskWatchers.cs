using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Notifications;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Notifications;
public sealed class TaskWatchers(TaskFlowDbContext db) : ITaskWatchers
{
    private static ProjectAccessException Missing() => new(ProjectAccessDecision.NotFound);
    private static TaskWatcherDto Map(TaskWatcher? value) => new(value?.IsWatching ?? false, (value?.Version ?? 0).ToString(CultureInfo.InvariantCulture));
    public async Task<TaskWatcherDto> GetAsync(WorkspaceReadContext c, long taskId, CancellationToken token = default)
    {
        await using var snapshot = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        if (!await WatcherAccess.Tasks(db,c).AnyAsync(t => t.Id == taskId,token)) throw Missing();
        var value = await db.TaskWatchers.AsNoTracking().SingleOrDefaultAsync(w => w.WorkspaceId == c.WorkspaceId && w.TaskId == taskId && w.UserId == c.ActorId,token);
        await snapshot.CommitAsync(token); return Map(value);
    }
    public async Task<TaskWatcherDto> SetAsync(WorkspaceReadContext c,long taskId,bool isWatching,long expectedVersion,CancellationToken token=default)
    {
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,token);
        var company=await db.Workspaces.FromSqlInterpolated($"SELECT * FROM Workspaces WHERE Id={c.WorkspaceId} FOR UPDATE").ToArrayAsync(token); if(company.Length!=1)throw Missing();
        var task=await WatcherAccess.Tasks(db,c).SingleOrDefaultAsync(t=>t.Id==taskId,token)??throw Missing();
        var value=await db.TaskWatchers.SingleOrDefaultAsync(w=>w.WorkspaceId==c.WorkspaceId&&w.TaskId==taskId&&w.UserId==c.ActorId,token);
        if(expectedVersion!=(value?.Version??0))throw new ResourceVersionException(409,"resource.conflict");
        if((value?.IsWatching??false)==isWatching){await transaction.CommitAsync(token);return Map(value);}
        // 90 watchers + at most 10 direct mentions bound comment fan-out to 100 recipients.
        if(isWatching&&await db.TaskWatchers.CountAsync(w=>w.WorkspaceId==c.WorkspaceId&&w.TaskId==taskId&&w.IsWatching,token)>=90)throw new TaskValidationException("isWatching","This task has reached its limit of 90 watchers.");
        if(value is null){value=new(){WorkspaceId=c.WorkspaceId,ProjectId=task.ProjectId,TaskId=taskId,UserId=c.ActorId};db.TaskWatchers.Add(value);}
        value.IsWatching=isWatching;await db.SaveChangesAsync(token);await transaction.CommitAsync(token);return Map(value);
    }
}
