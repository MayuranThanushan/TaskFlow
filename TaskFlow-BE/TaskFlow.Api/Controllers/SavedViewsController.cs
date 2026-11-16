using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
namespace TaskFlow.Api.Controllers;
[Authorize,ApiController,Route("api/v2/workspaces/{workspaceId}")]
[ServiceFilter(typeof(WorkspaceReadFilter))]
[ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class SavedViewsController(ISavedViews views) : ControllerBase
{
    private static long Id(string? s)=>WorkspaceReadFilter.TryId(s,out var id)?id:throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceReadContext Scope(string workspaceId)=>new(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)),Id(workspaceId));
    private void Query(params string[] allowed){foreach(var p in Request.Query)if(!allowed.Contains(p.Key,StringComparer.OrdinalIgnoreCase)||p.Value.Count!=1)ModelState.AddModelError(p.Key,"Unsupported or repeated query parameter.");}
    [HttpGet("views"),ProducesResponseType<SavedViewPage>(200)]
    public async Task<IActionResult> List(string workspaceId,[FromQuery] WorkspaceListQuery query,CancellationToken token){Query("page","pageSize");if(!ModelState.IsValid)return ValidationProblem(ModelState);return Ok(await views.ViewsAsync(Scope(workspaceId),query.Page,query.PageSize,token));}
    [HttpGet("views/{viewId}"),ProducesResponseType<SavedViewDto>(200)]
    public async Task<IActionResult> Get(string workspaceId,string viewId,CancellationToken token){Query();if(!ModelState.IsValid)return ValidationProblem(ModelState);return Ok(await views.ViewAsync(Scope(workspaceId),Id(viewId),token));}
    [HttpPost("views"),ServiceFilter(typeof(WorkspaceWriteFilter)),ProducesResponseType<SavedViewDto>(200)]
    public async Task<IActionResult> Create(string workspaceId,SavedViewRequest request,CancellationToken token)=>Ok(await views.SaveViewAsync(Scope(workspaceId),null,null,request.Definition(),token));
    [HttpPut("views/{viewId}"),ServiceFilter(typeof(WorkspaceWriteFilter)),ProducesResponseType<SavedViewDto>(200)]
    public async Task<IActionResult> Update(string workspaceId,string viewId,SavedViewUpdateRequest request,CancellationToken token)=>Ok(await views.SaveViewAsync(Scope(workspaceId),Id(viewId),request.ExpectedVersion,request.Definition(),token));
    [HttpDelete("views/{viewId}"),ServiceFilter(typeof(WorkspaceWriteFilter)),ProducesResponseType(204)]
    public async Task<IActionResult> Delete(string workspaceId,string viewId,SavedViewDeleteRequest request,CancellationToken token){await views.DeleteViewAsync(Scope(workspaceId),Id(viewId),request.ExpectedVersion,token);return NoContent();}
    [HttpGet("view-preferences/{context}"),ProducesResponseType<ViewPreferenceDto>(200)]
    public async Task<IActionResult> Preference(string workspaceId,string context,CancellationToken token){Query();if(!ModelState.IsValid)return ValidationProblem(ModelState);return Ok(await views.PreferenceAsync(Scope(workspaceId),context,token));}
    [HttpPut("view-preferences/{context}"),ServiceFilter(typeof(WorkspaceWriteFilter)),ProducesResponseType<ViewPreferenceDto>(200)]
    public async Task<IActionResult> SetPreference(string workspaceId,string context,ViewPreferenceRequest request,CancellationToken token)=>Ok(await views.SetPreferenceAsync(Scope(workspaceId),context,request.ExpectedVersion,request.SavedViewId is {} id?Id(id):null,token));
}
public class SavedViewRequest
{
    [Required,StringLength(100)]public string Name {get;set;}="";
    [Required,RegularExpression("^(work|tasks|board)$")]public string Surface {get;set;}="work";
    [StringLength(19)]public string? ProjectId {get;set;}
    public bool Shared {get;set;}
    [Required]public Dictionary<string,string> Filters {get;set;}=[];
    public SavedViewDefinition Definition()=>new(Name,Surface,ProjectId,Shared,Filters);
}
public sealed class SavedViewUpdateRequest : SavedViewRequest
{
    [Required,RegularExpression("^[1-9][0-9]{0,18}$")]public string ExpectedVersion {get;set;}="";
}
public sealed class SavedViewDeleteRequest
{
    [Required,RegularExpression("^[1-9][0-9]{0,18}$")]public string ExpectedVersion {get;set;}="";
}
public sealed class ViewPreferenceRequest
{
    [Required,RegularExpression("^(0|[1-9][0-9]{0,18})$")]public string ExpectedVersion {get;set;}="";
    [RegularExpression("^[1-9][0-9]{0,18}$")]public string? SavedViewId {get;set;}
}
