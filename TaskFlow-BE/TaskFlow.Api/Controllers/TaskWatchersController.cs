using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Notifications;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;
namespace TaskFlow.Api.Controllers;
[Authorize,ApiController,Route("api/v2/workspaces/{workspaceId}/tasks/{taskId}/watcher")]
[TaskWatcherFeature,ResponseCache(NoStore=true,Location=ResponseCacheLocation.None)]
public sealed class TaskWatchersController(ITaskWatchers watchers):ControllerBase
{
    private static long Id(string? value)=>WorkspaceReadFilter.TryId(value,out var id)?id:throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceReadContext Scope(string workspace)=>new(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)),Id(workspace));
    [HttpGet,ProducesResponseType<TaskWatcherDto>(200)]
    public async Task<IActionResult> Get(string workspaceId,string taskId,CancellationToken token)
    {if(Request.Query.Count!=0)return BadRequest(Middleware.ProblemResponses.Create(HttpContext,400));return Ok(await watchers.GetAsync(Scope(workspaceId),Id(taskId),token));}
    [HttpPut,EnableRateLimiting("governance"),ProducesResponseType<TaskWatcherDto>(200)]
    public async Task<IActionResult> Set(string workspaceId,string taskId,TaskWatcherRequest request,CancellationToken token)
    {
        if(Request.Query.Count!=0)return BadRequest(Middleware.ProblemResponses.Create(HttpContext,400));
        if(!long.TryParse(request.ExpectedVersion,NumberStyles.None,CultureInfo.InvariantCulture,out var version)){ModelState.AddModelError("expectedVersion","Reload the subscription revision.");return ValidationProblem(ModelState);}
        return Ok(await watchers.SetAsync(Scope(workspaceId),Id(taskId),request.IsWatching!.Value,version,token));
    }
}
public sealed class TaskWatcherRequest
{
    [Required]public bool? IsWatching{get;set;}
    [Required,RegularExpression("^(0|[1-9][0-9]{0,18})$")]public string ExpectedVersion{get;set;}="";
}
[AttributeUsage(AttributeTargets.Class)]
public sealed class TaskWatcherFeatureAttribute:Attribute,IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context,ResourceExecutionDelegate next)
    {
        var http=context.HttpContext;var configuration=http.RequestServices.GetRequiredService<IConfiguration>();
        if(!new[]{"WorkspaceReads:Enabled","WorkspaceWrites:Enabled","WorkspaceLifecycle:Enabled","Notifications:Enabled","Notifications:WatchersEnabled"}.All(k=>configuration.GetValue<bool>(k))){context.Result=new NotFoundResult();return;}
        if(!WorkspaceReadFilter.TryId(Convert.ToString(context.RouteData.Values["workspaceId"]),out var workspace)||!WorkspaceReadFilter.TryId(Convert.ToString(context.RouteData.Values["taskId"]),out var task)||!WorkspaceReadFilter.TryId(http.User.FindFirstValue(ClaimTypes.NameIdentifier),out var actor))throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        await http.RequestServices.GetRequiredService<IWorkspaceReads>().TaskAsync(new(actor,workspace),task,http.RequestAborted);await next();
    }
}
