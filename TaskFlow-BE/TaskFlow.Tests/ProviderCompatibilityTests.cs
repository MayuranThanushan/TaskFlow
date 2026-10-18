using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TaskFlow.Tests;

public class ProviderCompatibilityTests
{
    [Fact]
    public void MySqlProviderBuildsLongKeyIdentitySchemaWithoutConnecting()
    {
        var options = new DbContextOptionsBuilder<CompatibilityContext>()
            .UseMySQL("Server=127.0.0.1;Database=taskflow_compatibility;User ID=unused;Password=unused")
            .Options;
        using var context = new CompatibilityContext(options);

        Assert.Equal("MySql.EntityFrameworkCore", context.Database.ProviderName);
        var user = context.Model.FindEntityType(typeof(IdentityUser<long>));
        Assert.NotNull(user);
        Assert.Equal(typeof(long), user.FindProperty(nameof(IdentityUser<long>.Id))!.ClrType);

        var script = context.Database.GenerateCreateScript();
        Assert.Contains("AspNetUsers", script);
        Assert.Contains("bigint", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AspNetUserRoles", script);
    }

    private sealed class CompatibilityContext(DbContextOptions<CompatibilityContext> options)
        : IdentityDbContext<IdentityUser<long>, IdentityRole<long>, long>(options);
}
