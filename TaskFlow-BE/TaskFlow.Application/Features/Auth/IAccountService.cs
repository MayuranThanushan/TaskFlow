using System.Security.Claims;

namespace TaskFlow.Application.Features.Auth;

public sealed record ProfileDto(string Id, string DisplayName, string Email);
public sealed record RegistrationResult(ProfileDto? User, bool DuplicateEmail, Dictionary<string, string[]> Errors);

public interface IAccountService
{
    Task<RegistrationResult> RegisterAsync(string displayName, string email, string password);
    Task<ProfileDto?> LoginAsync(string email, string password);
    Task<ProfileDto?> CurrentAsync(ClaimsPrincipal principal);
    Task LogoutAsync(ClaimsPrincipal principal);
}
