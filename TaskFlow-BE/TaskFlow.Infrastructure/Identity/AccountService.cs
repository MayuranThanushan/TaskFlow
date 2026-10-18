using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using TaskFlow.Application.Features.Auth;

namespace TaskFlow.Infrastructure.Identity;

public sealed class AccountService(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
    UnknownUserPasswordCheck unknownUserCheck) : IAccountService
{
    public async Task<RegistrationResult> RegisterAsync(string displayName, string email, string password)
    {
        var user = new ApplicationUser { DisplayName = displayName.Trim(), Email = email.Trim(), UserName = email.Trim() };
        IdentityResult result;
        try { result = await users.CreateAsync(user, password); }
        catch (DbUpdateException exception) when (exception.InnerException is MySqlException { Number: 1062 })
        {
            return DuplicateEmail();
        }
        if (result.Succeeded) return new(ToProfile(user), false, []);
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
            return null;
        }
        var result = await signIn.PasswordSignInAsync(user, password, isPersistent: false, lockoutOnFailure: true);
        return result.Succeeded ? ToProfile(user) : null;
    }

    public async Task<ProfileDto?> CurrentAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal);
        return user is null ? null : ToProfile(user);
    }

    public async Task LogoutAsync(ClaimsPrincipal principal)
    {
        var user = await users.GetUserAsync(principal)
            ?? throw new InvalidOperationException("The authenticated account is unavailable.");
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) throw new InvalidOperationException("Session invalidation failed.");
        await signIn.SignOutAsync();
    }

    private static ProfileDto ToProfile(ApplicationUser user) => new(
        user.Id.ToString(CultureInfo.InvariantCulture), user.DisplayName, user.Email!);

    private static RegistrationResult DuplicateEmail() => new(null, true,
        new() { ["email"] = ["An account with this email already exists."] });
}
