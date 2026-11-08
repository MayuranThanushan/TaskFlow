using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Api.Middleware;
using TaskFlow.Application.Features.Auth;
namespace TaskFlow.Api.Controllers;

[ApiController, Authorize, Route("api/v1/auth/mfa"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class MfaController(IMfaService mfa) : ControllerBase
{
    [HttpGet, ProducesResponseType<MfaStatusDto>(200)]
    public async Task<IActionResult> Status() => await mfa.StatusAsync(User) is { } state ? Ok(state) : Unauthorized();
    [HttpPost("setup"), EnableRateLimiting("auth"), ProducesResponseType<MfaSetupDto>(200)]
    public Task<IActionResult> Setup(MfaProofRequest request) => Execute(async () => Ok(await mfa.BeginAsync(User, request.CurrentPassword, request.Code, request.RecoveryCode)));
    [HttpPost("confirm"), EnableRateLimiting("auth"), ProducesResponseType<MfaRecoveryDto>(200)]
    public Task<IActionResult> Confirm(MfaCodeRequest request) => Execute(async () => Ok(await mfa.ConfirmAsync(User, request.CurrentPassword, request.Code)));
    [HttpPost("recovery-codes"), EnableRateLimiting("auth"), ProducesResponseType<MfaRecoveryDto>(200)]
    public Task<IActionResult> RecoveryCodes(MfaCodeRequest request) => Execute(async () => Ok(await mfa.RotateAsync(User, request.CurrentPassword, request.Code)));
    [HttpPost("disable"), EnableRateLimiting("auth"), ProducesResponseType(204)]
    public Task<IActionResult> Disable(MfaProofRequest request) => Execute(async () => { await mfa.DisableAsync(User, request.CurrentPassword, request.Code, request.RecoveryCode); return NoContent(); });
    private async Task<IActionResult> Execute(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (MfaException e)
        {
            var problem = ProblemResponses.Create(HttpContext, 400); problem.Title = "MFA action could not be completed."; problem.Extensions["code"] = e.Code;
            return new ObjectResult(problem) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
        }
    }
}
public sealed class MfaProofRequest : AccountPasswordProofRequest
{
    [StringLength(6, MinimumLength = 6), RegularExpression("^[0-9]{6}$")] public string? Code { get; set; }
    [StringLength(64)] public string? RecoveryCode { get; set; }
}
public sealed class MfaCodeRequest : AccountPasswordProofRequest
{
    [Required, RegularExpression("^[0-9]{6}$"), StringLength(6)] public string Code { get; set; } = "";
}
