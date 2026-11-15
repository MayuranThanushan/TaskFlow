using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Features.Comments;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Workspaces;

namespace TaskFlow.Api.Controllers;
[Authorize, ApiController, Route("api/v2/workspaces/{workspaceId}/tasks/{taskId}/comments")]
[CommentFeature, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CommentsController(ITaskComments comments) : ControllerBase
{
    private static long Id(string? text) => WorkspaceReadFilter.TryId(text, out var id) ? id : throw new ProjectAccessException(ProjectAccessDecision.NotFound);
    private WorkspaceReadContext Scope(string workspace) => new(Id(User.FindFirstValue(ClaimTypes.NameIdentifier)), Id(workspace));
    private IActionResult? Query(params string[] allowed) => Request.Query.Any(q => !allowed.Contains(q.Key, StringComparer.OrdinalIgnoreCase) || q.Value.Count != 1) ? BadRequest(Middleware.ProblemResponses.Create(HttpContext, 400)) : null;
    [HttpGet, ProducesResponseType<CommentPage>(200)]
    public async Task<IActionResult> List(string workspaceId, string taskId, [FromQuery, Range(1, 51)] int page = 1, CancellationToken token = default) => Query("page") ?? Ok(await comments.ListAsync(Scope(workspaceId), Id(taskId), page, token));
    [HttpGet("mention-candidates"), EnableRateLimiting("governance"), ProducesResponseType<CommentCandidatesDto>(200)]
    public async Task<IActionResult> Candidates(string workspaceId, string taskId, [FromQuery, Required, StringLength(100, MinimumLength = 2)] string search, CancellationToken token) => Query("search") ?? Ok(await comments.CandidatesAsync(Scope(workspaceId), Id(taskId), search, token));
    [HttpGet("{commentId}/history"), ProducesResponseType<CommentHistoryDto>(200)]
    public async Task<IActionResult> History(string workspaceId, string taskId, string commentId, CancellationToken token) => Query() ?? Ok(await comments.HistoryAsync(Scope(workspaceId), Id(taskId), Id(commentId), token));
    [HttpPost, EnableRateLimiting("governance"), ProducesResponseType<TaskCommentDto>(201)]
    public async Task<IActionResult> Create(string workspaceId, string taskId, CommentCreateRequest request, CancellationToken token)
    {
        if (Query() is {} error) return error;
        var result = await comments.CreateAsync(Scope(workspaceId), Id(taskId), request.Body, token); return StatusCode(201, result);
    }
    [HttpPut("{commentId}"), EnableRateLimiting("governance"), ProducesResponseType<TaskCommentDto>(200)]
    public async Task<IActionResult> Edit(string workspaceId, string taskId, string commentId, CommentEditRequest request, CancellationToken token) => Query() ?? Ok(await comments.EditAsync(Scope(workspaceId), Id(taskId), Id(commentId), request.Body, Version(request.ExpectedVersion), token));
    [HttpDelete("{commentId}"), EnableRateLimiting("governance"), ProducesResponseType(204)]
    public async Task<IActionResult> Delete(string workspaceId, string taskId, string commentId, CommentDeleteRequest request, CancellationToken token)
    {
        if (Query() is {} error) return error;
        await comments.DeleteAsync(Scope(workspaceId), Id(taskId), Id(commentId), Version(request.ExpectedVersion), token); return NoContent();
    }
    private static long Version(string value) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version) && version > 0 ? version : throw new TaskFlow.Application.Features.Tasks.TaskValidationException("expectedVersion", "Reload the current comment revision.");
}
public class CommentCreateRequest { [Required, StringLength(5000, MinimumLength = 1)] public string Body { get; set; } = ""; }
public sealed class CommentEditRequest : CommentCreateRequest { [Required, RegularExpression("^[1-9][0-9]{0,18}$")] public string ExpectedVersion { get; set; } = ""; }
public sealed class CommentDeleteRequest { [Required, RegularExpression("^[1-9][0-9]{0,18}$")] public string ExpectedVersion { get; set; } = ""; }
// Task visibility is resolved before binding or query validation, preventing foreign task probes.
[AttributeUsage(AttributeTargets.Class)]
public sealed class CommentFeatureAttribute : Attribute, IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var http = context.HttpContext; var config = http.RequestServices.GetRequiredService<IConfiguration>();
        if (!new[] { "WorkspaceReads:Enabled", "WorkspaceWrites:Enabled", "WorkspaceLifecycle:Enabled", "Comments:Enabled" }.All(k => config.GetValue<bool>(k))) { context.Result = new NotFoundResult(); return; }
        if (!WorkspaceReadFilter.TryId(Convert.ToString(context.RouteData.Values["workspaceId"]), out var workspace) || !WorkspaceReadFilter.TryId(Convert.ToString(context.RouteData.Values["taskId"]), out var task) || !WorkspaceReadFilter.TryId(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actor)) throw new ProjectAccessException(ProjectAccessDecision.NotFound);
        await http.RequestServices.GetRequiredService<IWorkspaceReads>().TaskAsync(new(actor, workspace), task, http.RequestAborted);
        await next();
    }
}
