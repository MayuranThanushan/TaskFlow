using System.Security.Claims;
namespace TaskFlow.Application.Features.Auth;
public sealed record AccountCapabilitiesDto(bool EmailFlowsEnabled, bool RequireConfirmedEmail);
public sealed record AccountSecurityDto(bool EmailConfirmed, DateTimeOffset? ReauthenticatedUntilUtc);
public sealed record AccountActionResult(bool Succeeded, Dictionary<string, string[]> Errors);
public interface IAccountSecurity
{
    AccountCapabilitiesDto Capabilities { get; }
    Task RequestAsync(string email, bool recovery);
    Task<bool> ConfirmAsync(long userId, string token);
    Task<AccountActionResult> ResetAsync(long userId, string token, string password);
    Task<AccountActionResult> ChangeAsync(ClaimsPrincipal principal, string currentPassword, string newPassword);
    Task<bool> ReauthenticateAsync(ClaimsPrincipal principal, string password);
    Task<AccountSecurityDto?> StatusAsync(ClaimsPrincipal principal);
}
