using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace TaskFlow.Tests;

public sealed class TransportTests
{
    [Theory]
    [InlineData("Development", null, false)]
    [InlineData("Local", null, false)]
    [InlineData("UAT", null, true)]
    [InlineData("Production", null, true)]
    [InlineData("Development", "true", true)]
    [InlineData("Local", "true", true)]
    [InlineData("UAT", "false", false)]
    [InlineData("Production", "false", false)]
    public async Task EnvironmentDefaultAndOverrideControlHttpBootstrap(string environment, string? configured, bool strict)
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (configured is not null)
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["Transport:RequireHttps"] = configured }));
        });
        using var client = host.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        // Untrusted forwarded headers must not bypass strict transport.
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        var response = await client.GetAsync("/api/v1/auth/csrf");
        Assert.Equal(strict ? HttpStatusCode.BadRequest : HttpStatusCode.OK, response.StatusCode);
        Assert.Null(response.Headers.Location);
        if (!strict)
        {
            var cookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("TaskFlow.Csrf="));
            Assert.DoesNotContain("; secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.False(response.Headers.Contains("Strict-Transport-Security"));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);
        }
    }
}
