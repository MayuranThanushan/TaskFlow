using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using TaskFlow.Application.Features.Projects;
namespace TaskFlow.Api.Filters;

public sealed class ResourceVersionFilter(ResourcePrecondition precondition) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action) return;
        var controller = action.ControllerName; var name = action.ActionName;
        var project = controller == "Projects" && name is "Update" or "Delete"
            || controller == "WorkspaceWrites" && name is "UpdateProject" or "DeleteProject";
        var task = controller == "Tasks" && name is "Update" or "SetStatus" or "Delete"
            || controller == "WorkspaceWrites" && name is "UpdateTask" or "Status" or "DeleteTask";
        if (!project && !task) return;
        precondition.Resource = project ? "Project" : "Task";
        long.TryParse(Convert.ToString(context.RouteData.Values[project ? "projectId" : "taskId"]), out var id);
        precondition.ResourceId = id;
        precondition.Required = controller == "WorkspaceWrites";
        if (context.HttpContext.Request.Headers.TryGetValue("X-Expected-Version", out var header))
            precondition.Header = header.Count == 1 ? header[0] ?? "" : "invalid";
    }
    public void OnActionExecuted(ActionExecutedContext context) { }
}
