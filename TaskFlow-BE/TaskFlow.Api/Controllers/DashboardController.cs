using Microsoft.AspNetCore.Authorization;
using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Features.Dashboard;
namespace TaskFlow.Api.Controllers;

[Authorize]
[ApiController, Route("api/v1/dashboard")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class DashboardController(IDashboardService dashboard) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<DashboardDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        foreach (var parameter in Request.Query) ModelState.AddModelError(parameter.Key, "Dashboard does not support query parameters.");
        if (!ModelState.IsValid) return ValidationProblem(ModelState);
        var actor = long.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : (long?)null;
        return Ok(await dashboard.GetAsync(actor, cancellationToken));
    }
}
