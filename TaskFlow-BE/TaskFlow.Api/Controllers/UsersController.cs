using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Features.Auth;
namespace TaskFlow.Api.Controllers;

[ApiController, Route("api/v1/users/me")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class UsersController(IAccountService accounts) : ControllerBase
{
    [HttpPatch]
    [ProducesResponseType<ProfileDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(ProfileUpdateRequest request)
    {
        var result = await accounts.UpdateProfileAsync(User, request.DisplayName);
        if (result.Conflict) return Conflict();
        return result.User is null ? Unauthorized() : Ok(result.User);
    }
}

public sealed class ProfileUpdateRequest
{
    private string displayName = "";
    [Required, StringLength(100)]
    public string DisplayName { get => displayName; set => displayName = value?.Trim() ?? ""; }
}
