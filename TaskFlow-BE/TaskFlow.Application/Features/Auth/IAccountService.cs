using System.Security.Claims;

namespace TaskFlow.Application.Features.Auth;

public sealed record ProfileDto(string Id, string DisplayName, string Email);
public sealed record RegistrationResult(ProfileDto? User, bool DuplicateEmail, Dictionary<string, string[]> Errors);

public sealed record ProfileUpdateResult(ProfileDto? User, bool Conflict = false);

public interface IAccountService
{
    Task<RegistrationResult> RegisterAsync(string displayName, string email, string password);
    Task<ProfileDto?> LoginAsync(string email, string password);
    Task<ProfileDto?> CurrentAsync(ClaimsPrincipal principal);
    Task<ProfileUpdateResult> UpdateProfileAsync(ClaimsPrincipal principal, string displayName);
    Task LogoutAsync(ClaimsPrincipal principal);
}
