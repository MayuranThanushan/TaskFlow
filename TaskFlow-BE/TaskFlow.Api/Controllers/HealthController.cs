using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TaskFlow.Api.Controllers;

[ApiController, AllowAnonymous]
[Route("api/v1/health")]
public sealed class HealthController(HealthCheckService healthChecks) : ControllerBase
{
    /// <summary>Checks process liveness without checking database readiness.</summary>
    [HttpGet("live")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<HealthResponse>> Live(CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(_ => false, cancellationToken);
        return Ok(new HealthResponse(report.Status.ToString()));
    }
}

public sealed record HealthResponse(string Status);
