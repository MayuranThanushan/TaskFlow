using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Projects;

// Called only under the existing project/company writer gate. Rank is never supplied by a client.
public static class BoardOrdering
{
    public const long Step = 1024;
    public const int MaximumRebalance = 500;
    private static ResourceVersionException Conflict(string code="board.position_conflict") => new(409,code);
    public static async Task<long> AppendAsync(TaskFlowDbContext db,long project,long? workspace,TaskState status,long? exclude,CancellationToken token)
    {
        var maximum=await db.Tasks.Where(t=>t.ProjectId==project&&t.WorkspaceId==workspace&&t.Status==status&&t.Id!=exclude).Select(t=>(long?)t.BoardRank).MaxAsync(token)??0;
        if(maximum>long.MaxValue-Step)throw Conflict("board.rank_exhausted");
        return Math.Max(0,maximum)+Step;
    }
    public static async Task MoveAsync(TaskFlowDbContext db,TaskItem task,TaskMove move,Action<ActivityAction,long> record,CancellationToken token)
    {
        if(db.Database.CurrentTransaction is null||task.WorkspaceId is null)throw new InvalidOperationException("Board moves require a scoped transaction.");
        if(!Enum.IsDefined(move.Status)||move.PreviousTaskId is <=0||move.NextTaskId is <=0||move.PreviousVersion is <=0||move.NextVersion is <=0
            ||(move.PreviousTaskId is null)!=(move.PreviousVersion is null)||(move.NextTaskId is null)!=(move.NextVersion is null)
            ||move.PreviousTaskId==task.Id||move.NextTaskId==task.Id||move.PreviousTaskId is not null&&move.PreviousTaskId==move.NextTaskId)
            throw new TaskValidationException("position","Choose distinct adjacent cards and their captured revisions.");
        var rows=await db.Tasks.AsNoTracking().Where(t=>t.WorkspaceId==task.WorkspaceId&&t.ProjectId==task.ProjectId&&t.Status==move.Status&&t.Id!=task.Id
                &&(t.AssigneeId==null||db.WorkspaceMemberships.Any(m=>m.WorkspaceId==task.WorkspaceId&&m.UserId==t.AssigneeId)
                    &&(t.Project.OwnerId==t.AssigneeId||db.ProjectMembers.Any(m=>m.WorkspaceId==task.WorkspaceId&&m.ProjectId==task.ProjectId&&m.UserId==t.AssigneeId&&m.RevokedAtUtc==null&&m.Role!=MembershipRole.Viewer))))
            .OrderBy(t=>t.BoardRank).ThenByDescending(t=>t.Id).Select(t=>new {t.Id,t.BoardRank,t.Version}).Take(MaximumRebalance+1).ToArrayAsync(token);
        if(rows.Length>=MaximumRebalance)throw Conflict("board.capacity_exceeded");
        int Neighbor(long? id,long? version)
        {
            if(id is null)return -1;
            var index=Array.FindIndex(rows,r=>r.Id==id);
            if(index<0)throw new ProjectAccessException(ProjectAccessDecision.NotFound);
            if(rows[index].Version!=version)throw Conflict();
            return index;
        }
        var before=Neighbor(move.PreviousTaskId,move.PreviousVersion);var after=Neighbor(move.NextTaskId,move.NextVersion);
        var at=before+1;
        if(after!=(at==rows.Length?-1:at))throw Conflict();
        var current=rows.Count(r=>r.BoardRank<task.BoardRank||r.BoardRank==task.BoardRank&&r.Id>task.Id);
        if(task.Status==move.Status&&at==current)return;
        var lower=before<0?0:rows[before].BoardRank;
        var upper=after<0?(lower<=long.MaxValue-Step?lower+Step:lower):rows[after].BoardRank;
        if(lower>=0&&upper>lower&&upper-lower>1)task.BoardRank=lower+(upper-lower)/2;
        else
        {
            var order=rows.Select(r=>r.Id).ToList();order.Insert(at,task.Id);
            var ids=rows.Select(r=>r.Id).ToArray();
            var tracked=await db.Tasks.Where(t=>t.WorkspaceId==task.WorkspaceId&&t.ProjectId==task.ProjectId&&ids.Contains(t.Id)).ToDictionaryAsync(t=>t.Id,token);
            for(var i=0;i<order.Count;i++)
            {
                var item=order[i]==task.Id?task:tracked[order[i]];var rank=checked((i+1)*Step);
                if(item.BoardRank==rank)continue;item.BoardRank=rank;
                if(item.Id!=task.Id)record(ActivityAction.TaskRankRebalanced,item.Id);
            }
        }
        task.Status=move.Status;record(ActivityAction.TaskMoved,task.Id);
    }
}
