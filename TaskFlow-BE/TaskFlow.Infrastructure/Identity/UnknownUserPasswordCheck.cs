using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace TaskFlow.Infrastructure.Identity;

// Perform a real hash verification for unknown accounts as well as existing accounts.
public sealed class UnknownUserPasswordCheck
{
    private readonly ApplicationUser user = new();
    private readonly PasswordHasher<ApplicationUser> hasher;
    private readonly string hash;

    public UnknownUserPasswordCheck(IOptions<PasswordHasherOptions> options)
    {
        hasher = new(options);
        hash = hasher.HashPassword(user, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
    }

    public void Verify(string password) => hasher.VerifyHashedPassword(user, hash, password);
}
