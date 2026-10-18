using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TaskFlow.Application.Features.Auth;
using TaskFlow.Api.Middleware;

namespace TaskFlow.Api.Controllers;

[ApiController, Route("api/v1/auth")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthController(IAccountService accounts) : ControllerBase
{
    [AllowAnonymous, HttpPost("register"), EnableRateLimiting("auth")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(RegisterRequest request)
    {
        var result = await accounts.RegisterAsync(request.DisplayName, request.Email, request.Password);
        if (result.User is not null) return Created("/api/v1/auth/me", result.User);
        var status = result.DuplicateEmail ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
        var problem = new ValidationProblemDetails(result.Errors) { Status = status, Title = "Account registration failed." };
        ProblemResponses.Enrich(HttpContext, problem);
        var response = new ObjectResult(problem) { StatusCode = status };
        response.ContentTypes.Add("application/problem+json");
        return response;
    }

    [AllowAnonymous, HttpPost("login"), EnableRateLimiting("auth")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Login(LoginRequest request)
    {
        var profile = await accounts.LoginAsync(request.Email, request.Password);
        return profile is null ? Unauthorized() : Ok(profile);
    }

    [Authorize, HttpGet("me")]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Me()
    {
        var profile = await accounts.CurrentAsync(User);
        return profile is null ? Unauthorized() : Ok(profile);
    }

    [Authorize, HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout()
    {
        await accounts.LogoutAsync(User);
        return NoContent();
    }
}

public sealed class RegisterRequest : LoginRequest
{
    private string displayName = "";
    [Required, StringLength(100)]
    public string DisplayName { get => displayName; set => displayName = value?.Trim() ?? ""; }
}

public class LoginRequest
{
    private string email = "";
    [Required, EmailAddress, StringLength(256)]
    public string Email { get => email; set => email = value?.Trim() ?? ""; }
    [Required, StringLength(128)]
    public string Password { get; set; } = "";
}
