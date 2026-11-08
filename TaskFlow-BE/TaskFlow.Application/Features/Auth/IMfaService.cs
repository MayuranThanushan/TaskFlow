using System.Security.Claims;
namespace TaskFlow.Application.Features.Auth;

public sealed record MfaStatusDto(bool EnrollmentAvailable, bool Enabled, int RecoveryCodesRemaining, bool RequiredForAdministration, DateTimeOffset? VerifiedUntilUtc, DateTimeOffset? RecoveryVerifiedUntilUtc);
public sealed record MfaSetupDto(string SharedKey, DateTime ExpiresAtUtc);
public sealed record MfaRecoveryDto(string[] RecoveryCodes);
public sealed class MfaException(string code) : Exception("MFA action could not be completed.") { public string Code { get; } = code; }
public interface IMfaService
{
    Task<MfaStatusDto?> StatusAsync(ClaimsPrincipal principal);
    Task<MfaSetupDto> BeginAsync(ClaimsPrincipal principal, string password, string? code, string? recoveryCode);
    Task<MfaRecoveryDto> ConfirmAsync(ClaimsPrincipal principal, string password, string code);
    Task<MfaRecoveryDto> RotateAsync(ClaimsPrincipal principal, string password, string code);
    Task DisableAsync(ClaimsPrincipal principal, string password, string? code, string? recoveryCode);
}
