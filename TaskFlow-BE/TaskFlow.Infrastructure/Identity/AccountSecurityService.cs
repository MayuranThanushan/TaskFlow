using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using TaskFlow.Application.Features.Auth;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Application.Features.Email;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Identity;

public sealed class AccountSecuritySettings(IConfiguration configuration, string environment)
{
    public bool Enabled { get; } = configuration.GetValue<bool>("AccountSecurity:EmailFlowsEnabled") && environment is "Development" or "Staging";
    public bool RequireConfirmedEmail { get; } = configuration.GetValue<bool>("AccountSecurity:RequireConfirmedEmail");
}
public sealed class AccountSecurityService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
    TaskFlowDbContext db, IDataProtectionProvider protection, AccountSecuritySettings settings, TimeProvider clock, ISecurityEvents security) : IAccountSecurity
{
    public const string RecentClaim = "taskflow:recent_authentication";
    private sealed record ActionToken(long UserId, bool Recovery, string IdentityToken, DateTimeOffset Expires);
    public AccountCapabilitiesDto Capabilities => new(settings.Enabled, settings.Enabled && settings.RequireConfirmedEmail);
    private IDataProtector ActionProtection => protection.CreateProtector("TaskFlow.AccountActions.v1");
    private string Issue(ApplicationUser user, bool recovery, string raw) => ActionProtection.Protect(JsonSerializer.Serialize(new ActionToken(user.Id, recovery, raw, clock.GetUtcNow().AddHours(1))));
    private string? Read(long userId, bool recovery, string token)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ActionToken>(ActionProtection.Unprotect(token));
            return value is not null && value.UserId == userId && value.Recovery == recovery && value.Expires > clock.GetUtcNow() ? value.IdentityToken : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { return null; }
    }
    private async Task<ApplicationUser?> Lock(long id)
    {
        var rows = await db.Users.FromSqlInterpolated($"SELECT * FROM `AspNetUsers` WHERE `Id`={id} FOR UPDATE").ToListAsync();
        var user = rows.SingleOrDefault(); if (user is not null) await db.Entry(user).ReloadAsync(); return user;
    }
    internal static async Task InvalidateMail(TaskFlowDbContext db, long id)
    {
        await db.AccountEmailJobs.Where(x => x.UserId == id && (x.State == "Pending" || x.State == "Leased"))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Cancelled").SetProperty(x => x.ProtectedPayload, (string?)null).SetProperty(x => x.LeaseToken, (string?)null).SetProperty(x => x.LeaseExpiresAtUtc, (DateTime?)null).SetProperty(x => x.LastCode, "account.invalidated"));
    }
    public async Task RequestAsync(string email, bool recovery)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            var found = await users.FindByEmailAsync(email.Trim());
            if (found is null) return;
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            var user = await Lock(found.Id); if (user is null) return;
            var now = clock.GetUtcNow().UtcDateTime;
            // Same response for absent, unverified, cooling down, capped and accepted accounts.
            if (recovery && !user.EmailConfirmed || !recovery && user.EmailConfirmed || user.TwoFactorEnabled) return;
            if (await db.AccountEmailJobs.AnyAsync(j => j.UserId == user.Id && j.CreatedAtUtc > now.AddMinutes(-1)) || await db.AccountEmailJobs.CountAsync(j => j.UserId == user.Id && j.CreatedAtUtc > now.AddHours(-1)) >= 3) return;
            var raw = recovery ? await users.GeneratePasswordResetTokenAsync(user) : await users.GenerateEmailConfirmationTokenAsync(user);
            var job = new AccountEmailJob { UserId = user.Id, Kind = recovery ? "PasswordRecovery" : "EmailVerification", CreatedAtUtc = now, AvailableAtUtc = now, ExpiresAtUtc = now.AddHours(1) };
            var request = new EmailRequest { AccountUserId = user.Id, MessageId = job.MessageId, Recipient = user.Email!, DisplayName = user.DisplayName, Template = recovery ? EmailTemplate.PasswordRecovery : EmailTemplate.EmailVerification, SecurityToken = Issue(user, recovery, raw) };
            job.ProtectedPayload = protection.CreateProtector("TaskFlow.AccountMail.v1", user.Id.ToString(CultureInfo.InvariantCulture), job.MessageId).Protect(JsonSerializer.Serialize(request));
            db.AccountEmailJobs.Add(job); db.SecurityEvents.Add(SecurityRecords.Create(recovery ? SecurityAction.RecoveryRequested : SecurityAction.VerificationRequested, SecurityOutcome.Succeeded, user.Id));
            await db.SaveChangesAsync(); await transaction.CommitAsync();
        }
        finally { var remaining = TimeSpan.FromMilliseconds(350) - timer.Elapsed; if (remaining > TimeSpan.Zero) await Task.Delay(remaining); }
    }
    public async Task<bool> ConfirmAsync(long userId, string token)
    {
        var raw = Read(userId, false, token); if (raw is null) return false;
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var user = await Lock(userId); if (user is null || user.EmailConfirmed) return false;
        if (!(await users.ConfirmEmailAsync(user, raw)).Succeeded) return false;
        if (!(await users.UpdateSecurityStampAsync(user)).Succeeded) throw new InvalidOperationException("Account invalidation failed.");
        await InvalidateMail(db, user.Id);
        db.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.EmailVerified, SecurityOutcome.Succeeded, user.Id));
        await db.SaveChangesAsync(); await transaction.CommitAsync(); await signIn.SignOutAsync(); return true;
    }
    public async Task<AccountActionResult> ResetAsync(long userId, string token, string password)
    {
        var raw = Read(userId, true, token); if (raw is null) return InvalidLink();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var user = await Lock(userId); if (user is null || !user.EmailConfirmed || user.TwoFactorEnabled) return InvalidLink();
        var result = await users.ResetPasswordAsync(user, raw, password); if (!result.Succeeded) return Errors(result);
        await InvalidateMail(db, user.Id);
        db.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.RecoveryCompleted, SecurityOutcome.Succeeded, user.Id));
        await db.SaveChangesAsync(); await transaction.CommitAsync(); await signIn.SignOutAsync(); return new(true, []);
    }
    public async Task<AccountActionResult> ChangeAsync(ClaimsPrincipal principal, string currentPassword, string newPassword)
    {
        var id = users.GetUserId(principal); if (!long.TryParse(id, out var number)) return new(false, []);
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        var user = await Lock(number); if (user is null || user.TwoFactorEnabled) return new(false, []);
        var proof = await signIn.CheckPasswordSignInAsync(user, currentPassword, true);
        if (!proof.Succeeded)
        {
            await transaction.CommitAsync(); await security.WriteAsync(SecurityAction.PasswordChanged, proof.IsLockedOut ? SecurityOutcome.LockedOut : SecurityOutcome.Denied, number, number);
            return new(false, new() { ["currentPassword"] = ["Current password could not be verified."] });
        }
        var result = await users.ChangePasswordAsync(user, currentPassword, newPassword); if (!result.Succeeded) return Errors(result);
        await InvalidateMail(db, user.Id); db.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.PasswordChanged, SecurityOutcome.Succeeded, number, number));
        await db.SaveChangesAsync(); await transaction.CommitAsync(); await signIn.SignOutAsync(); return new(true, []);
    }
    public async Task<bool> ReauthenticateAsync(ClaimsPrincipal principal, string password)
    {
        var user = await users.GetUserAsync(principal); if (user is null || user.TwoFactorEnabled) return false;
        var result = await signIn.CheckPasswordSignInAsync(user, password, true);
        await security.WriteAsync(SecurityAction.Reauthentication, result.Succeeded ? SecurityOutcome.Succeeded : result.IsLockedOut ? SecurityOutcome.LockedOut : SecurityOutcome.Denied, user.Id, user.Id);
        if (!result.Succeeded) return false;
        await signIn.SignInWithClaimsAsync(user, false, [new Claim(RecentClaim, clock.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))]); return true;
    }
    public async Task<AccountSecurityDto?> StatusAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal); if (user is null) return null;
        DateTimeOffset? until = null;
        if (long.TryParse(principal.FindFirstValue(RecentClaim), out var issued) && issued >= 0 && issued <= clock.GetUtcNow().ToUnixTimeSeconds())
        { var expiry = DateTimeOffset.FromUnixTimeSeconds(issued).AddMinutes(5); if (expiry > clock.GetUtcNow()) until = expiry; }
        return new(user.EmailConfirmed, until);
    }
    private static AccountActionResult InvalidLink() => new(false, new() { ["token"] = ["This link is invalid or expired. Request a new one."] });
    private static AccountActionResult Errors(IdentityResult result) => result.Errors.Any(e => e.Code == "InvalidToken") ? InvalidLink() : new(false, new() { ["newPassword"] = result.Errors.Where(e => e.Code.StartsWith("Password", StringComparison.Ordinal)).Select(e => e.Description).DefaultIfEmpty("The account changed. Please try again.").ToArray() });
}
