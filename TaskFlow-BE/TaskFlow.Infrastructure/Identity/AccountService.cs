using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using TaskFlow.Application.Features.Auth;
using TaskFlow.Application.Features.Activity;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Infrastructure.Identity;

public sealed class AccountService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
    UnknownUserPasswordCheck unknownUserCheck, TaskFlowDbContext database, ISecurityEvents security) : IAccountService
{
    public async Task<RegistrationResult> RegisterAsync(string displayName, string email, string password)
    {
        var user = new ApplicationUser { DisplayName = displayName.Trim(), Email = email.Trim(), UserName = email.Trim() };
        await using var transaction = await database.Database.BeginTransactionAsync();
        IdentityResult result;
        try { result = await users.CreateAsync(user, password); }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            await transaction.RollbackAsync();
            await security.WriteAsync(SecurityAction.Registration, SecurityOutcome.Denied, null);
            return DuplicateEmail();
        }
        if (result.Succeeded)
        {
            database.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.Registration, SecurityOutcome.Succeeded, user.Id));
            await database.SaveChangesAsync(); await transaction.CommitAsync();
            return new(ToProfile(user), false, []);
        }
        await transaction.RollbackAsync();
        await security.WriteAsync(SecurityAction.Registration, SecurityOutcome.Denied, null);
        if (result.Errors.Any(error => error.Code is "DuplicateEmail" or "DuplicateUserName")) return DuplicateEmail();
        var errors = result.Errors.GroupBy(error => error.Code.StartsWith("Password", StringComparison.Ordinal) ? "password" : "email")
            .ToDictionary(group => group.Key, group => group.Select(error => error.Description).ToArray());
        return new(null, false, errors);
    }

    public async Task<ProfileDto?> LoginAsync(string email, string password)
    {
        var user = await users.FindByEmailAsync(email.Trim());
        if (user is null)
        {
            unknownUserCheck.Verify(password);
            await security.WriteAsync(SecurityAction.Login, SecurityOutcome.Denied, null);
            return null;
        }
        var result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (!result.Succeeded || user.TwoFactorEnabled)
        {
            await security.WriteAsync(SecurityAction.Login, result.IsLockedOut ? SecurityOutcome.LockedOut : SecurityOutcome.Denied, user.Id);
            return null;
        }
        // Persist credential verification before issuing a session; a sink failure issues no cookie.
        await security.WriteAsync(SecurityAction.Login, SecurityOutcome.Succeeded, user.Id, user.Id);
        await signIn.SignInAsync(user, isPersistent: false);
        return ToProfile(user);
    }

    public async Task<ProfileDto?> CurrentAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? null : ToProfile(user);
    }

    public async Task<ProfileUpdateResult> UpdateProfileAsync(ClaimsPrincipal principal, string displayName)
    {
        var name = displayName.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Invalid display name.", nameof(displayName));
        var user = await users.GetUserAsync(principal);
        if (user is null) return new(null);
        if (user.DisplayName == name) return new(ToProfile(user));
        await using var transaction = await database.Database.BeginTransactionAsync();
        user.DisplayName = name;
        var result = await users.UpdateAsync(user);
        if (result.Succeeded)
        {
            database.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.ProfileChanged, SecurityOutcome.Succeeded, user.Id, user.Id));
            await database.SaveChangesAsync(); await transaction.CommitAsync(); return new(ToProfile(user));
        }
        await transaction.RollbackAsync();
        if (result.Errors.Any(error => error.Code == "ConcurrencyFailure")) return new(null, true);
        throw new InvalidOperationException("Profile update failed.");
    }

    public async Task LogoutAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal)
            ?? throw new InvalidOperationException("The authenticated account is unavailable.");
        await using var transaction = await database.Database.BeginTransactionAsync();
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException("Session invalidation failed.");
        await AccountSecurityService.InvalidateMail(database, user.Id);
        database.SecurityEvents.Add(SecurityRecords.Create(SecurityAction.Logout, SecurityOutcome.Succeeded, user.Id, user.Id));
        await database.SaveChangesAsync(); await transaction.CommitAsync();
        await signIn.SignOutAsync();
    }

    private static ProfileDto ToProfile(ApplicationUser user) => new(
        user.Id.ToString(CultureInfo.InvariantCulture), user.DisplayName, user.Email!);

    private static RegistrationResult DuplicateEmail() => new(null, true,
        new() { ["email"] = ["An account with this email already exists."] });
}
