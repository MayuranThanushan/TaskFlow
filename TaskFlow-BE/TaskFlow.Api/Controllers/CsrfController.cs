using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace TaskFlow.Api.Controllers;

[ApiController, AllowAnonymous, Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class CsrfController(IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("csrf")]
    public ActionResult<CsrfDto> Token()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new CsrfDto(tokens.RequestToken!, tokens.HeaderName!));
    }
}

public sealed record CsrfDto(string RequestToken, string HeaderName);
