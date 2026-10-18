using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed class DeploymentTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "taskflow-deployment-" + Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> Host(string? connection = null)
    {
        Directory.CreateDirectory(Path.Combine(directory, "wwwroot", "assets"));
        Directory.CreateDirectory(Path.Combine(directory, "wwwroot", "api"));
        File.WriteAllText(Path.Combine(directory, "wwwroot", "api", "shadow.js"), "must-not-be-served");
        File.WriteAllText(Path.Combine(directory, "wwwroot", "index.html"), "<!doctype html><div id=\"root\">deployment-shell</div>");
        File.WriteAllText(Path.Combine(directory, "wwwroot", "assets", "app-123.js"), "console.log('asset');");
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production").UseWebRoot(Path.Combine(directory, "wwwroot"));
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DataProtection:KeysPath"] = Path.Combine(directory, "keys"),
                ["ConnectionStrings:TaskFlow"] = connection ?? "Server=127.0.0.1;Port=1;Database=taskflow_unavailable;User ID=unused;Password=never-return-this;Connection Timeout=1"
            }));
        });
    }

    [Fact]
    public async Task ProductionServesSpaDeepLinksButPreservesApiProblemsAndMethods()
    {
        await using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        foreach (var path in new[] { "/", "/login", "/register", "/dashboard?view=mine", "/unknown-client-route" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
            Assert.Contains("deployment-shell", await response.Content.ReadAsStringAsync());
            Assert.True(response.Headers.CacheControl?.NoCache);
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        }
        foreach (var path in new[] { "/api/v1/missing", "/api/missing", "/api/shadow.js", "/openapi/v1.json", "/assets/missing.js" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.PostAsync("/api/v1/health/live", null)).StatusCode);
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/dashboard"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
    }

    [Fact]
    public async Task PublishedAssetsHaveImmutableCachingAndSecurityHeaders()
    {
        await using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var response = await client.GetAsync("/assets/app-123.js");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("immutable", response.Headers.CacheControl!.ToString());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task UnavailableDatabaseIsNotReadyButLivenessRemainsHealthy()
    {
        await using var host = Host();
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var ready = await client.GetAsync("/api/v1/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.True(ready.Headers.CacheControl?.NoStore);
        var text = await ready.Content.ReadAsStringAsync();
        Assert.Equal("Unhealthy", JsonDocument.Parse(text).RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("never-return-this", text);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);
    }

    [Fact]
    public async Task ProductionRedirectsHttpPagesAndRejectsHttpAuthenticationWithoutRedirect()
    {
        await using var host = Host().WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?> { ["HTTPS_PORT"] = "8443" })));
        using var client = host.CreateClient(new() { BaseAddress = new("http://localhost"), AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.TemporaryRedirect, (await client.GetAsync("/dashboard")).StatusCode);
        var auth = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.BadRequest, auth.StatusCode);
        Assert.Null(auth.Headers.Location);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

[Collection(MySqlCollection.Name)]
public sealed class DatabaseReadinessTests(MySqlFixture fixture)
{
    [MySqlFact, Trait("Category", "MySql")]
    public async Task UnknownAppliedMigrationBlocksReadinessWithoutExposingSchemaDetails()
    {
        await using var database = fixture.CreateContext();
        const string future = "20990101000000_UnrecognizedRelease";
        await database.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ({future}, {"10.0.11"})");
        try
        {
            await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                { ["ConnectionStrings:TaskFlow"] = fixture.ConnectionString })));
            using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
            var response = await client.GetAsync("/api/v1/health/ready");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain(future, await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/health/live")).StatusCode);
        }
        finally { await database.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM __EFMigrationsHistory WHERE MigrationId={future}"); }
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task MigratedDatabaseReportsReadyWithoutSigningIn()
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            { ["ConnectionStrings:TaskFlow"] = fixture.ConnectionString })));
        using var client = host.CreateClient(new() { BaseAddress = new("https://localhost") });
        var ready = await client.GetAsync("/api/v1/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.True(ready.Headers.CacheControl?.NoStore);
        Assert.Equal("Healthy", (await ready.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }
}
