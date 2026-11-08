using System.Data;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Application.Features.Auth;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Identity;

public sealed class MfaSettings(IConfiguration configuration)
{
    public bool EnrollmentEnabled => configuration.GetValue<bool>("Mfa:Enabled");
    public bool RequireAdministrators => configuration.GetValue<bool>("Mfa:RequireForCompanyAdministrators");
}
public static class MfaProof
{
    public const string Method = "taskflow:mfa";
    public const string Issued = "taskflow:mfa_at";
    public static DateTimeOffset? Until(ClaimsPrincipal principal, TimeProvider clock)
        => ProofUntil(principal, clock, "totp");
    public static DateTimeOffset? RecoveryUntil(ClaimsPrincipal principal, TimeProvider clock)
        => ProofUntil(principal, clock, "recovery");
    private static DateTimeOffset? ProofUntil(ClaimsPrincipal principal, TimeProvider clock, string method)
    {
        if (principal.FindFirstValue(Method) != method || !long.TryParse(principal.FindFirstValue(Issued), out var seconds)) return null;
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        return seconds >= 0 && seconds <= now && now - seconds < 300 ? DateTimeOffset.FromUnixTimeSeconds(seconds).AddMinutes(5) : null;
    }
    public static Claim[] Claims(TimeProvider clock, bool recovery = false) => [new(Method, recovery ? "recovery" : "totp"), new(Issued, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)), new(AccountSecurityService.RecentClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))];
}
public sealed class MfaService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, TaskFlowDbContext db,
    IDataProtectionProvider protection, MfaSettings settings, TimeProvider clock) : IMfaService
{
    private const string SetupClaim = "taskflow:mfa_setup";
    private sealed record Pending(string Secret, string Stamp, string Nonce);
    private IDataProtector Protector(long id, string purpose) => protection.CreateProtector("TaskFlow.Mfa.v1", id.ToString(CultureInfo.InvariantCulture), purpose);
    internal async Task<ApplicationUser?> Lock(long id)
    {
        var rows = await db.Users.FromSqlInterpolated($"SELECT * FROM `AspNetUsers` WHERE `Id`={id} FOR UPDATE").ToListAsync();
        var user = rows.SingleOrDefault(); if (user is not null) await db.Entry(user).ReloadAsync(); return user;
    }
    private async Task<ApplicationUser> Subject(ClaimsPrincipal principal)
    {
        if (!long.TryParse(users.GetUserId(principal), out var id)) throw new MfaException("mfa.unavailable");
        var user = await Lock(id);
        if (user is null || principal.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType) != user.SecurityStamp) throw new MfaException("mfa.unavailable");
        return user;
    }
    internal async Task<bool> Required(long user) => settings.RequireAdministrators && await db.WorkspaceMemberships.AnyAsync(m => m.UserId == user && m.State == WorkspaceMembershipState.Active && (m.Role == WorkspaceRole.Owner || m.Role == WorkspaceRole.Admin));
    public async Task<MfaStatusDto?> StatusAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal); if (user is null) return null;
        var state = await db.AccountMfa.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == user.Id);
        return new(settings.EnrollmentEnabled || user.TwoFactorEnabled, user.TwoFactorEnabled, user.TwoFactorEnabled && state is not null ? Hashes(state).Length : 0, await Required(user.Id), MfaProof.Until(principal, clock), MfaProof.RecoveryUntil(principal, clock));
    }
    private static string[] Hashes(AccountMfa state) => JsonSerializer.Deserialize<string[]>(state.RecoveryHashes) ?? [];
    // Caller holds the account row lock and commits code consumption with its successful operation.
    internal async Task<bool> Verify(ApplicationUser user, string? code, string? recoveryCode = null)
    {
        if (!user.TwoFactorEnabled || (!string.IsNullOrWhiteSpace(code) && !string.IsNullOrWhiteSpace(recoveryCode))) return false;
        var state = await db.AccountMfa.SingleOrDefaultAsync(m => m.UserId == user.Id);
        if (state?.ProtectedSecret is null) return false;
        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            var normalized = recoveryCode.Replace("-", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
            if (normalized.Length != 32 || !normalized.All(Uri.IsHexDigit)) return false;
            var hash = MfaCodes.RecoveryHash(user.Id, normalized); var hashes = Hashes(state);
            var match = Array.FindIndex(hashes, h => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(h), Encoding.ASCII.GetBytes(hash)));
            if (match < 0) return false;
            state.RecoveryHashes = JsonSerializer.Serialize(hashes.Where((_, i) => i != match)); return true;
        }
        try
        {
            var secret = Protector(user.Id, "active").Unprotect(state.ProtectedSecret);
            var step = MfaCodes.Match(secret, code, clock.GetUtcNow(), state.LastAcceptedStep);
            if (step is null) return false; state.LastAcceptedStep = step.Value; return true;
        }
        catch (CryptographicException) { return false; }
    }
    private async Task Check(ApplicationUser user, string password, string? code, string? recovery = null, bool secondFactor = true)
    {
        var allowed = await signIn.CanSignInAsync(user) && !await users.IsLockedOutAsync(user);
        if (allowed && await users.CheckPasswordAsync(user, password) && (!secondFactor || !user.TwoFactorEnabled || await Verify(user, code, recovery))) return;
        if (allowed && !(await users.AccessFailedAsync(user)).Succeeded) throw new InvalidOperationException("Account failure tracking failed.");
        db.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.MfaAction, SecurityOutcome.Denied, user.Id, user.Id));
        await db.SaveChangesAsync();
        // Persist bounded failures even though the operation is rejected.
        await db.Database.CurrentTransaction!.CommitAsync();
        throw new MfaException("mfa.proof_invalid");
    }
    private async Task Renew(ApplicationUser user)
    {
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded || !(await users.ResetAccessFailedCountAsync(user)).Succeeded) throw new InvalidOperationException("Account invalidation failed.");
        await AccountSecurityService.InvalidateMail(db, user.Id);
    }
    private void Audit(ApplicationUser user, SecurityAction action) => db.SecurityEvents.Add(SecurityRecords.Create(action, SecurityOutcome.Succeeded, user.Id, user.Id));
    public async Task<MfaSetupDto> BeginAsync(ClaimsPrincipal principal, string password, string? code, string? recoveryCode)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var user = await Subject(principal);
        if (!settings.EnrollmentEnabled && !user.TwoFactorEnabled) throw new MfaException("mfa.enrollment_disabled");
        if (!user.EmailConfirmed) throw new MfaException("mfa.email_unverified");
        // A just-consumed final recovery code must still permit device restoration. This
        // session proof is accepted only to BEGIN enrollment, never for administration/disable.
        var recovering = MfaProof.RecoveryUntil(principal, clock) is not null && string.IsNullOrWhiteSpace(code) && string.IsNullOrWhiteSpace(recoveryCode);
        await Check(user, password, code, recoveryCode, secondFactor: !recovering);
        var state = await db.AccountMfa.SingleOrDefaultAsync(m => m.UserId == user.Id);
        if (state is null) { state = new() { UserId = user.Id }; db.AccountMfa.Add(state); }
        var secret = MfaCodes.NewSecret(); var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        state.PendingProtectedSecret = Protector(user.Id, "pending").Protect(JsonSerializer.Serialize(new Pending(secret, user.SecurityStamp!, nonce)));
        state.PendingExpiresAtUtc = clock.GetUtcNow().AddMinutes(10).UtcDateTime;
        Audit(user, SecurityAction.MfaSetupStarted); await db.SaveChangesAsync(); await transaction.CommitAsync();
        // Bind confirmation to this session; starting another setup replaces the pending enrollment.
        var claims = principal.Claims.Where(c => c.Type is MfaProof.Method or MfaProof.Issued or AccountSecurityService.RecentClaim).Append(new Claim(SetupClaim, nonce));
        await signIn.SignInWithClaimsAsync(user, false, claims); return new(secret, state.PendingExpiresAtUtc.Value);
    }
    public async Task<MfaRecoveryDto> ConfirmAsync(ClaimsPrincipal principal, string password, string code)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var user = await Subject(principal); await Check(user, password, null, secondFactor: false);
        var state = await db.AccountMfa.SingleOrDefaultAsync(m => m.UserId == user.Id); Pending? pending = null;
        try { if (state?.PendingProtectedSecret is not null) pending = JsonSerializer.Deserialize<Pending>(Protector(user.Id, "pending").Unprotect(state.PendingProtectedSecret)); } catch (Exception e) when (e is CryptographicException or JsonException) { }
        var step = pending is not null ? MfaCodes.Match(pending.Secret, code, clock.GetUtcNow(), -1) : null;
        if (state is null || pending is null || step is null || state.PendingExpiresAtUtc is not { } expiry || expiry <= clock.GetUtcNow().UtcDateTime || pending.Stamp != user.SecurityStamp || pending.Nonce != principal.FindFirstValue(SetupClaim))
        {
            await users.AccessFailedAsync(user); AuditDenied(user); await db.SaveChangesAsync(); await transaction.CommitAsync(); throw new MfaException("mfa.setup_invalid");
        }
        state.ProtectedSecret = Protector(user.Id, "active").Protect(pending.Secret); state.PendingProtectedSecret = null; state.PendingExpiresAtUtc = null; state.LastAcceptedStep = step.Value;
        user.TwoFactorEnabled = true; var codes = Recoveries(state); await Renew(user); Audit(user, SecurityAction.MfaEnabled);
        await db.SaveChangesAsync(); await transaction.CommitAsync(); await signIn.SignInWithClaimsAsync(user, false, MfaProof.Claims(clock)); return new(codes);
    }
    private void AuditDenied(ApplicationUser user) => db.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.MfaAction, SecurityOutcome.Denied, user.Id, user.Id));
    private static string[] Recoveries(AccountMfa state)
    {
        var codes = Enumerable.Range(0, 10).Select(_ => MfaCodes.NewRecoveryCode()).ToArray();
        state.RecoveryHashes = JsonSerializer.Serialize(codes.Select(c => MfaCodes.RecoveryHash(state.UserId, c))); return codes;
    }
    public async Task<MfaRecoveryDto> RotateAsync(ClaimsPrincipal principal, string password, string code)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var user = await Subject(principal); if (!user.TwoFactorEnabled) throw new MfaException("mfa.unavailable");
        await Check(user, password, code); var state = await db.AccountMfa.SingleAsync(m => m.UserId == user.Id);
        var codes = Recoveries(state); await Renew(user); Audit(user, SecurityAction.MfaRecoveryRotated);
        await db.SaveChangesAsync(); await transaction.CommitAsync(); await signIn.SignInWithClaimsAsync(user, false, MfaProof.Claims(clock)); return new(codes);
    }
    public async Task DisableAsync(ClaimsPrincipal principal, string password, string? code, string? recoveryCode)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var user = await Subject(principal); if (!user.TwoFactorEnabled) throw new MfaException("mfa.unavailable");
        if (await Required(user.Id)) throw new MfaException("mfa.required_for_administration");
        await Check(user, password, code, recoveryCode);
        var state = await db.AccountMfa.SingleAsync(m => m.UserId == user.Id);
        state.ProtectedSecret = null; state.PendingProtectedSecret = null; state.PendingExpiresAtUtc = null; state.RecoveryHashes = "[]"; state.LastAcceptedStep = -1;
        user.TwoFactorEnabled = false; await Renew(user); Audit(user, SecurityAction.MfaDisabled);
        await db.SaveChangesAsync(); await transaction.CommitAsync(); await signIn.SignOutAsync();
    }
}
