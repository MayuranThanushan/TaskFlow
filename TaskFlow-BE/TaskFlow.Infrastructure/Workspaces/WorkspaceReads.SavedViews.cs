using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Tasks;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Workspaces;
public sealed partial class WorkspaceReads
{
    private IQueryable<SavedView> VisibleViews(WorkspaceReadContext s) => database.SavedViews.AsNoTracking().Where(v => v.WorkspaceId == s.WorkspaceId
        && (v.UserId == s.ActorId || v.Shared) && (v.ProjectId == null || Projects(s).Any(p => p.Id == v.ProjectId)));
    private async Task<bool> ViewAdmin(WorkspaceReadContext s, CancellationToken t) => await database.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == s.WorkspaceId && m.UserId == s.ActorId && m.State == WorkspaceMembershipState.Active && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Admin), t);
    private static SavedViewDto ViewDto(SavedView v, long actor, bool admin) => new(Id(v.Id), Id(v.UserId), v.Name, v.Surface, v.ProjectId is {} p ? Id(p) : null, v.Shared, JsonSerializer.Deserialize<Dictionary<string,string>>(v.FiltersJson)!, Id(v.Version), v.Shared ? admin : v.UserId == actor);
    public Task<SavedViewPage> ViewsAsync(WorkspaceReadContext s, int page, int size, CancellationToken t) => Read(s, async () => {
        Page(page,size); var query=VisibleViews(s); var count=await query.CountAsync(t); var admin=await ViewAdmin(s,t);
        var rows=await query.OrderBy(v=>v.Name).ThenBy(v=>v.Id).Skip((page-1)*size).Take(size).ToArrayAsync(t);
        return new SavedViewPage(rows.Select(v=>ViewDto(v,s.ActorId,admin)).ToArray(),page,size,count,(int)Math.Ceiling(count/(double)size));
    },t);
    public Task<SavedViewDto> ViewAsync(WorkspaceReadContext s,long id,CancellationToken t) => Read(s,async()=> {
        var v=await VisibleViews(s).SingleOrDefaultAsync(v=>v.Id==id,t)??throw Missing();
        ValidateView(new(v.Name,v.Surface,v.ProjectId is {} p?Id(p):null,v.Shared,JsonSerializer.Deserialize<Dictionary<string,string>>(v.FiltersJson)!));
        return ViewDto(v,s.ActorId,await ViewAdmin(s,t));
    },t);
    private async Task LockViews(WorkspaceReadContext s,CancellationToken t)
    {
        await database.Workspaces.FromSqlInterpolated($"SELECT * FROM `Workspaces` WHERE `Id` = {s.WorkspaceId} FOR UPDATE").ToListAsync(t);
        await database.WorkspaceMemberships.FromSqlInterpolated($"SELECT * FROM `WorkspaceMemberships` WHERE `WorkspaceId` = {s.WorkspaceId} AND `UserId` = {s.ActorId} FOR UPDATE").AsNoTracking().ToListAsync(t);
        await AuthorizeAsync(s,t);
    }
    private void RecordViewAudit(WorkspaceReadContext s,SavedView row,string action) => database.WorkspaceAuditLogs.Add(new WorkspaceAuditLog{WorkspaceId=s.WorkspaceId,ActorUserId=s.ActorId,TargetUserId=s.ActorId,ProjectId=row.ProjectId,Action=action,SubjectType="SavedView",SubjectId=Id(row.Id),CreatedAtUtc=clock.GetUtcNow().UtcDateTime});
    private static void ViewVersion(string? expected,long current) { var precondition=new ResourcePrecondition{Header=expected,Required=true};precondition.Check(current); }
    private static TaskValidationException InvalidView(string message) => new("view",message);
    private static long? ValidateView(SavedViewDefinition d)
    {
        if(d.Name is null || string.IsNullOrWhiteSpace(d.Name) || d.Name.Trim().Length>100 || d.Surface is not ("work" or "tasks" or "board") || d.Filters is null || d.Filters.Count>12)throw InvalidView("Supply a name, supported surface and bounded filters.");
        long? project=null;
        if(d.ProjectId is not null){if(!long.TryParse(d.ProjectId,NumberStyles.None,CultureInfo.InvariantCulture,out var p)||p<=0||Id(p)!=d.ProjectId)throw InvalidView("Invalid project.");project=p;}
        if(d.Surface!="work"&&project is null)throw InvalidView("A task list or board requires a project.");
        var allowed=d.Surface switch {"work"=>new[]{"search","assignment","queue","status","priority","fromDate","throughDate","fromUtc","toUtc","pageSize","sortBy","sortDirection"},"tasks"=>new[]{"search","status","priority","assigneeId","pageSize","sortBy","sortDirection"},_=>Array.Empty<string>()};
        foreach(var (key,value) in d.Filters)
        {
            if(!allowed.Contains(key,StringComparer.Ordinal)||value is null||value.Length==0||value.Length>200)throw InvalidView("Unsupported or invalid saved filter.");
            bool valid=key switch {
                "search"=>value.Trim()==value,
                "assignment"=>new[]{"mine","unassigned","all"}.Contains(value),"queue"=>new[]{"open","due","overdue","all"}.Contains(value),
                "status"=>new[]{"ToDo","InProgress","Done"}.Contains(value),"priority"=>new[]{"Low","Medium","High"}.Contains(value),
                "sortDirection"=>new[]{"asc","desc"}.Contains(value),"sortBy"=>(d.Surface=="work"?new[]{"title","dueDateUtc","createdAtUtc"}:new[]{"title","priority","dueDateUtc","createdAtUtc","rank"}).Contains(value),
                "pageSize"=>int.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var size)&&size>=1&&size<=100,
                "assigneeId"=>long.TryParse(value,NumberStyles.None,CultureInfo.InvariantCulture,out var user)&&user>0,
                "fromDate" or "throughDate"=>DateOnly.TryParseExact(value,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)&&day.Year>=1000&&day.Year<9999,
                "fromUtc" or "toUtc"=>System.Text.RegularExpressions.Regex.IsMatch(value,@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")&&DateTimeOffset.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)&&date.UtcDateTime.Year>=1000,
                _=>false};
            if(!valid)throw InvalidView("Unsupported or invalid saved filter.");
        }
        if(d.Filters.ContainsKey("fromDate")&&d.Filters.ContainsKey("fromUtc")||d.Filters.ContainsKey("throughDate")&&d.Filters.ContainsKey("toUtc"))throw InvalidView("Choose calendar or timestamp bounds.");
        if(d.Filters.TryGetValue("fromDate",out var from)&&d.Filters.TryGetValue("throughDate",out var to)&&string.CompareOrdinal(from,to)>0)throw InvalidView("Inverted calendar range.");
        if(d.Filters.TryGetValue("fromUtc",out from)&&d.Filters.TryGetValue("toUtc",out to)&&DateTimeOffset.Parse(from,CultureInfo.InvariantCulture)>DateTimeOffset.Parse(to,CultureInfo.InvariantCulture))throw InvalidView("Inverted timestamp range.");
        return project;
    }
    public async Task<SavedViewDto> SaveViewAsync(WorkspaceReadContext s,long? id,string? expected,SavedViewDefinition d,CancellationToken t)
    {
        await using var tx=await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,t);await LockViews(s,t);
        SavedView? row=null;var admin=await ViewAdmin(s,t);
        if(id is {} key){row=await VisibleViews(s).SingleOrDefaultAsync(v=>v.Id==key,t)??throw Missing();if(row.Shared?!admin:row.UserId!=s.ActorId)throw new ProjectAccessException(ProjectAccessDecision.Forbidden);ViewVersion(expected,row.Version);database.Attach(row);}
        var project=ValidateView(d);if(project is {} p)await RequireProject(s,p,t);
        if(d.Shared&&!admin)throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
        if(row is null){if(await database.SavedViews.CountAsync(v=>v.WorkspaceId==s.WorkspaceId&&v.UserId==s.ActorId,t)>=100)throw InvalidView("Limit of 100 saved views per member and company reached.");row=new SavedView{WorkspaceId=s.WorkspaceId,UserId=s.ActorId};database.SavedViews.Add(row);}
        var wasShared=row.Shared;
        row.Name=d.Name.Trim();row.Surface=d.Surface;row.ProjectId=project;row.Shared=d.Shared;row.FiltersJson=JsonSerializer.Serialize(d.Filters.OrderBy(x=>x.Key).ToDictionary(x=>x.Key,x=>x.Value));
        await database.SaveChangesAsync(t);
        if(wasShared||row.Shared){RecordViewAudit(s,row,id is null?"SavedViewCreated":"SavedViewUpdated");await TaskFlow.Infrastructure.Outbox.OutboxEnqueue.CaptureAsync(database,t);await database.SaveChangesAsync(t);}
        await tx.CommitAsync(t);return ViewDto(row,s.ActorId,admin);
    }
    public async Task DeleteViewAsync(WorkspaceReadContext s,long id,string expected,CancellationToken t)
    {
        await using var tx=await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,t);await LockViews(s,t);
        var row=await VisibleViews(s).SingleOrDefaultAsync(v=>v.Id==id,t)??throw Missing();if(row.Shared?!await ViewAdmin(s,t):row.UserId!=s.ActorId)throw new ProjectAccessException(ProjectAccessDecision.Forbidden);
        ViewVersion(expected,row.Version);if(row.Shared)RecordViewAudit(s,row,"SavedViewDeleted");database.SavedViews.Remove(row);if(row.Shared)await TaskFlow.Infrastructure.Outbox.OutboxEnqueue.CaptureAsync(database,t);await database.SaveChangesAsync(t);await tx.CommitAsync(t);
    }
    private static (string Surface,long? Project) ViewContext(string context)
    {
        if(context=="work")return ("work",null);
        var split=context.Split(':');if(split.Length==2&&split[0] is "tasks" or "board"&&long.TryParse(split[1],NumberStyles.None,CultureInfo.InvariantCulture,out var id)&&id>0&&Id(id)==split[1])return(split[0],id);
        throw InvalidView("Invalid preference context.");
    }
    private async Task<ViewPreferenceDto> PreferenceDto(WorkspaceReadContext s,ViewPreference? row,string context,CancellationToken t)
    {
        var target=ViewContext(context);
        var available=row?.SavedViewId is null||await VisibleViews(s).AnyAsync(v=>v.Id==row.SavedViewId&&v.Surface==target.Surface&&v.ProjectId==target.Project,t);
        return new(context,available&&row?.SavedViewId is {} id?Id(id):null,row is null?"0":Id(row.Version),available);
    }
    public Task<ViewPreferenceDto> PreferenceAsync(WorkspaceReadContext s,string context,CancellationToken t)=>Read(s,async()=>{
        var target=ViewContext(context);if(target.Project is {} p)await RequireProject(s,p,t);
        return await PreferenceDto(s,await database.ViewPreferences.AsNoTracking().SingleOrDefaultAsync(p=>p.WorkspaceId==s.WorkspaceId&&p.UserId==s.ActorId&&p.Context==context,t),context,t);
    },t);
    public async Task<ViewPreferenceDto> SetPreferenceAsync(WorkspaceReadContext s,string context,string expected,long? viewId,CancellationToken t)
    {
        await using var tx=await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted,t);await LockViews(s,t);var target=ViewContext(context);if(target.Project is {} p)await RequireProject(s,p,t);
        if(viewId is {} id){var view=await VisibleViews(s).SingleOrDefaultAsync(v=>v.Id==id,t)??throw Missing();if(view.Surface!=target.Surface||view.ProjectId!=target.Project)throw InvalidView("The default must match this screen and project.");}
        var row=await database.ViewPreferences.SingleOrDefaultAsync(p=>p.WorkspaceId==s.WorkspaceId&&p.UserId==s.ActorId&&p.Context==context,t);
        if(row is null){if(expected!="0")throw new ResourceVersionException(409,"resource.conflict");row=new(){WorkspaceId=s.WorkspaceId,UserId=s.ActorId,Context=context};database.ViewPreferences.Add(row);}else {if(expected=="0")throw new ResourceVersionException(409,"resource.conflict");ViewVersion(expected,row.Version);}
        row.SavedViewId=viewId;await database.SaveChangesAsync(t);await tx.CommitAsync(t);return await PreferenceDto(s,row,context,t);
    }
}
