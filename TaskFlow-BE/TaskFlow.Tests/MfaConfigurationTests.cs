using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
namespace TaskFlow.Tests;

public class MfaConfigurationTests
{
    [Theory]
    [InlineData("WorkspaceReads:Enabled")]
    [InlineData("WorkspaceWrites:Enabled")]
    [InlineData("WorkspaceLifecycle:Enabled")]
    public void AdministratorRequirementCannotLeaveLegacyWritesOpen(string missingFlag)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mfa:RequireForCompanyAdministrators"] = "true", ["WorkspaceReads:Enabled"] = "true",
                ["WorkspaceWrites:Enabled"] = "true", ["WorkspaceLifecycle:Enabled"] = "true", [missingFlag] = "false"
            })));
        var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("legacy company writes must be closed", failure.Message);
    }
}
