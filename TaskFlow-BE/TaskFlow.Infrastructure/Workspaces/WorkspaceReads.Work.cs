using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
namespace TaskFlow.Infrastructure.Workspaces;
public sealed partial class WorkspaceReads
{
    public Task<WorkPage> WorkAsync(WorkspaceReadContext context,WorkOptions options,CancellationToken token=default)=>Read(context,async()=>
    {
        Page(options.Page,options.PageSize);
        if(options.Assignment is not ("mine" or "unassigned" or "all")||options.Queue is not ("open" or "due" or "overdue" or "all")
            ||options.Search?.Length>200||options.ProjectId is <=0||options.Status is {} state&&!Enum.IsDefined(state)||options.Priority is {} priority&&!Enum.IsDefined(priority)
            ||options.SortBy is not ("dueDateUtc" or "createdAtUtc" or "title")||options.SortDirection is not ("asc" or "desc")||options.FromUtc>options.ToUtc)throw new ArgumentException("Invalid work query.");
        if(options.ProjectId is {} project)await RequireProject(context,project,token);
        var now=clock.GetUtcNow().UtcDateTime;now=new DateTime(now.Ticks-now.Ticks%10,DateTimeKind.Utc);
        var through=now>DateTime.MaxValue.AddDays(-7)?DateTime.MaxValue:now.AddDays(7);
        var query=Tasks(context);
        if(options.Assignment=="mine")query=query.Where(t=>t.AssigneeId==context.ActorId);
        if(options.Assignment=="unassigned")query=query.Where(t=>t.AssigneeId==null);
        if(options.ProjectId is {} id)query=query.Where(t=>t.ProjectId==id);
        var search=options.Search?.Trim();if(!string.IsNullOrEmpty(search))query=query.Where(t=>t.Title.Contains(search)||t.Project.Name.Contains(search));
        if(options.Status is {} status)query=query.Where(t=>t.Status==status);
        if(options.Priority is {} p)query=query.Where(t=>t.Priority==p);
        // Inclusive bounds at the database's microsecond precision. Never round a
        // lower bound down and accidentally admit a deadline before the request.
        if(options.FromUtc is {} from)
        {
            var extra=(10-from.Ticks%10)%10;
            if(from.Ticks>DateTime.MaxValue.Ticks-extra)query=query.Where(t=>false);
            else {var lower=new DateTime(from.Ticks+extra,DateTimeKind.Utc);query=query.Where(t=>t.DueDateUtc!=null&&t.DueDateUtc>=lower);}
        }
        if(options.ToUtc is {} to){var upper=new DateTime(to.Ticks-to.Ticks%10,DateTimeKind.Utc);query=query.Where(t=>t.DueDateUtc!=null&&t.DueDateUtc<=upper);}
        var totals=await query.GroupBy(t=>1).Select(g=>new {Total=g.Count(),Open=g.Count(t=>t.Status!=TaskState.Done),Completed=g.Count(t=>t.Status==TaskState.Done),
            Due=g.Count(t=>t.Status!=TaskState.Done&&t.DueDateUtc>=now&&t.DueDateUtc<=through),Overdue=g.Count(t=>t.Status!=TaskState.Done&&t.DueDateUtc<now)}).SingleOrDefaultAsync(token);
        var counts=new WorkCountsDto(totals?.Open??0,totals?.Due??0,totals?.Overdue??0,totals?.Completed??0,totals?.Total??0);
        if(options.Queue=="open")query=query.Where(t=>t.Status!=TaskState.Done);
        if(options.Queue=="due")query=query.Where(t=>t.Status!=TaskState.Done&&t.DueDateUtc>=now&&t.DueDateUtc<=through);
        if(options.Queue=="overdue")query=query.Where(t=>t.Status!=TaskState.Done&&t.DueDateUtc<now);
        var total=options.Queue switch {"open"=>counts.Open,"due"=>counts.Due,"overdue"=>counts.Overdue,_=>counts.Total};
        var asc=options.SortDirection=="asc";
        var ordered=options.SortBy switch
        {
            "title"=>asc?query.OrderBy(t=>t.Title):query.OrderByDescending(t=>t.Title),
            "createdAtUtc"=>asc?query.OrderBy(t=>t.CreatedAtUtc):query.OrderByDescending(t=>t.CreatedAtUtc),
            _=>asc?query.OrderBy(t=>t.DueDateUtc==null).ThenBy(t=>t.DueDateUtc):query.OrderBy(t=>t.DueDateUtc==null).ThenByDescending(t=>t.DueDateUtc)
        };
        var rows=await ordered.ThenByDescending(t=>t.Id).Skip((options.Page-1)*options.PageSize).Take(options.PageSize).Select(t=>new {t.Id,t.ProjectId,t.Title,t.Status,t.Priority,t.AssigneeId,t.DueDateUtc,t.CreatedAtUtc,t.UpdatedAtUtc,t.Version,ProjectName=t.Project.Name,AssigneeName=database.Users.Where(u=>u.Id==t.AssigneeId).Select(u=>u.DisplayName).FirstOrDefault()}).ToArrayAsync(token);
        return new WorkPage(rows.Select(r=>new WorkItemDto(new TaskWorkDto(Id(r.Id),Id(r.ProjectId),r.Title,r.Status.ToString(),r.Priority.ToString(),r.AssigneeId is {} assignee?Id(assignee):null,r.DueDateUtc,r.CreatedAtUtc,r.UpdatedAtUtc,Id(r.Version)),r.ProjectName,r.AssigneeName)).ToArray(),options.Page,options.PageSize,total,(int)Math.Ceiling(total/(double)options.PageSize),counts,now,through);
    },token);
}
