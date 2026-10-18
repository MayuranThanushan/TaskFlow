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

    /// <summary>Checks MySQL connectivity, applied migrations and Identity table access without changing data.</summary>
    [HttpGet("ready")]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<HealthResponse>(StatusCodes.Status503ServiceUnavailable)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<HealthResponse>> Ready(CancellationToken cancellationToken)
    {
        var report = await healthChecks.CheckHealthAsync(check => check.Tags.Contains("ready"), cancellationToken);
        return StatusCode(report.Status == HealthStatus.Healthy ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable, new HealthResponse(report.Status.ToString()));
    }

}

public sealed record HealthResponse(string Status);
