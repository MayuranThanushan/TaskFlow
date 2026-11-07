using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Application.Features.Auth;
using TaskFlow.Api.Middleware;
namespace TaskFlow.Api.Controllers;
[ApiController, Route("api/v1/auth"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountSecurityController(IAccountSecurity accounts) : ControllerBase
{
    [AllowAnonymous, HttpGet("capabilities"), ProducesResponseType<AccountCapabilitiesDto>(200)]
    public IActionResult Capabilities() => Ok(accounts.Capabilities);
    [Authorize, HttpGet("security"), ProducesResponseType<AccountSecurityDto>(200)]
    public async Task<IActionResult> Security() => await accounts.StatusAsync(User) is { } state ? Ok(state) : Unauthorized();
    [AllowAnonymous, HttpPost("request-verification"), EnableRateLimiting("auth"), ProducesResponseType(202)]
    public async Task<IActionResult> RequestVerification(AccountEmailRequest request) => await RequestMail(request.Email, false);
    [AllowAnonymous, HttpPost("forgot-password"), EnableRateLimiting("auth"), ProducesResponseType(202)]
    public async Task<IActionResult> ForgotPassword(AccountEmailRequest request) => await RequestMail(request.Email, true);
    private async Task<IActionResult> RequestMail(string email, bool recovery)
    {
        if (!accounts.Capabilities.EmailFlowsEnabled) return StatusCode(503);
        await accounts.RequestAsync(email, recovery); return Accepted();
    }
    [AllowAnonymous, HttpPost("confirm-email"), EnableRateLimiting("auth"), ProducesResponseType(204)]
    public async Task<IActionResult> ConfirmEmail(AccountTokenRequest request)
    {
        if (!accounts.Capabilities.EmailFlowsEnabled) return StatusCode(503);
        return await accounts.ConfirmAsync(long.Parse(request.UserId), request.Token) ? NoContent() : Failure(new() { ["token"] = ["This link is invalid or expired. Request a new one."] });
    }
    [AllowAnonymous, HttpPost("reset-password"), EnableRateLimiting("auth"), ProducesResponseType(204)]
    public async Task<IActionResult> ResetPassword(AccountResetRequest request)
    {
        if (!accounts.Capabilities.EmailFlowsEnabled) return StatusCode(503);
        var result = await accounts.ResetAsync(long.Parse(request.UserId), request.Token, request.NewPassword, request.Code, request.RecoveryCode); return result.Succeeded ? NoContent() : Failure(result.Errors);
    }
    [Authorize, HttpPost("change-password"), EnableRateLimiting("auth"), ProducesResponseType(204)]
    public async Task<IActionResult> ChangePassword(AccountPasswordChangeRequest request)
    {
        var result = await accounts.ChangeAsync(User, request.CurrentPassword, request.NewPassword, request.Code); return result.Succeeded ? NoContent() : Failure(result.Errors);
    }
    [Authorize, HttpPost("reauthenticate"), EnableRateLimiting("auth"), ProducesResponseType(204)]
    public async Task<IActionResult> Reauthenticate(AccountReauthenticationRequest request) => await accounts.ReauthenticateAsync(User, request.CurrentPassword, request.Code) ? NoContent() : Failure(new() { ["currentPassword"] = ["Password could not be verified."] });
    private IActionResult Failure(Dictionary<string, string[]> errors)
    {
        var problem = new ValidationProblemDetails(errors) { Status = 400, Title = "Account action could not be completed." }; ProblemResponses.Enrich(HttpContext, problem);
        var result = new ObjectResult(problem) { StatusCode = 400 }; result.ContentTypes.Add("application/problem+json"); return result;
    }
}
public sealed class AccountEmailRequest
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
}
public class AccountTokenRequest : IValidatableObject
{
    [Required, RegularExpression("^[1-9][0-9]{0,18}$")] public string UserId { get; set; } = "";
    [Required, StringLength(4096, MinimumLength = 20)] public string Token { get; set; } = "";
    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    { if (!long.TryParse(UserId, out var id) || id <= 0) yield return new ValidationResult("Invalid account reference.", [nameof(UserId)]); }
}
public sealed class AccountResetRequest : AccountTokenRequest
{
    [StringLength(6, MinimumLength = 6), RegularExpression("^[0-9]{6}$")] public string? Code { get; set; }
    [StringLength(64)] public string? RecoveryCode { get; set; }
    [Required, StringLength(128, MinimumLength = 12)] public string NewPassword { get; set; } = "";
}
public class AccountPasswordProofRequest
{
    [Required, StringLength(128)] public string CurrentPassword { get; set; } = "";
}
public class AccountReauthenticationRequest : AccountPasswordProofRequest
{
    [StringLength(6, MinimumLength = 6), RegularExpression("^[0-9]{6}$")] public string? Code { get; set; }
}
public sealed class AccountPasswordChangeRequest : AccountReauthenticationRequest
{
    [Required, StringLength(128, MinimumLength = 12)] public string NewPassword { get; set; } = "";
}
