using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

[Collection(MySqlCollection.Name)]
public partial class AuthTests(MySqlFixture fixture)
{
    private const string Password = "ExampleOnly-Pass123!";

    [MySqlFact, Trait("Category", "MySql")]
    public async Task RegistrationLoginFreshCsrfLogoutAndReplayFollowTheContract()
    {
        await using var host = new AuthHost(fixture);
        using var client = host.Client();
        var email = host.Email();
        var token = await Csrf(client);
        var registration = await Post(client, "register", new { displayName = " Alex ", email = $" {email} ", password = Password }, token);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var profile = await registration.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.String, profile.GetProperty("id").ValueKind);
        Assert.Equal("Alex", profile.GetProperty("displayName").GetString());
        Assert.Equal(3, profile.EnumerateObject().Count());
        await Problem(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        await Problem(await client.GetAsync("/_tests/protected"), HttpStatusCode.Unauthorized);

        var login = await Post(client, "login", new { email, password = Password }, token);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookieHeader = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-TaskFlow.Auth="));
        foreach (var flag in new[] { "secure", "httponly", "samesite=lax", "path=/" })
            Assert.Contains(flag, cookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", cookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookieHeader, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/_tests/protected")).StatusCode);
        await Problem(await client.GetAsync("/_tests/forbidden"), HttpStatusCode.Forbidden);
        await Problem(await Post(client, "logout", null, token), HttpStatusCode.BadRequest);
        token = await Csrf(client);
        var logout = await Post(client, "logout", null, token);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal("", await logout.Content.ReadAsStringAsync());
        await Problem(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        using var replay = host.Client(cookies: false);
        replay.DefaultRequestHeaders.Add("Cookie", cookieHeader.Split(';')[0]);
        await Problem(await replay.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task LogoutInvalidatesOtherDeviceSessionsImmediately()
    {
        await using var host = new AuthHost(fixture);
        using var first = host.Client();
        using var second = host.Client();
        var email = await Register(host, first);
        Assert.Equal(HttpStatusCode.OK, (await Post(first, "login", new { email, password = Password }, await Csrf(first))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(second, "login", new { email, password = Password }, await Csrf(second))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(first, "logout", null, await Csrf(first))).StatusCode);
        await Problem(await second.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task PasswordFailuresAndLockedOrUnknownAccountsHaveTheSamePublicError()
    {
        await using var host = new AuthHost(fixture);
        using var client = host.Client();
        var email = await Register(host, client);
        var token = await Csrf(client);
        for (var attempt = 0; attempt < 5; attempt++)
            await Problem(await Post(client, "login", new { email, password = "Wrong-Pass123!" }, token), HttpStatusCode.Unauthorized);
        var locked = await Problem(await Post(client, "login", new { email, password = Password }, token), HttpStatusCode.Unauthorized);
        var unknown = await Problem(await Post(client, "login", new { email = host.Email(), password = Password }, token), HttpStatusCode.Unauthorized);
        Assert.Equal(locked.GetProperty("title").GetString(), unknown.GetProperty("title").GetString());
        await using var context = fixture.CreateContext();
        var user = await context.Users.SingleAsync(value => value.Email == email);
        Assert.True(user.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.True(user.LockoutEnd <= DateTimeOffset.UtcNow.AddMinutes(16));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task SessionExpiresAfterEightHoursAndValidationDoesNotRenewIt()
    {
        await using var host = new AuthHost(fixture);
        using var client = host.Client();
        var email = await Register(host, client);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode);
        host.Clock.Now = host.Clock.Now.AddHours(7);
        var beforeExpiry = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, beforeExpiry.StatusCode);
        Assert.False(beforeExpiry.Headers.TryGetValues("Set-Cookie", out var cookies)
            && cookies.Any(value => value.StartsWith("__Host-TaskFlow.Auth=")));
        host.Clock.Now = host.Clock.Now.AddHours(1).AddSeconds(1);
        await Problem(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task AuthRequiresHttpsAndClientBoundCsrfTokens()
    {
        await using var host = new AuthHost(fixture);
        using var client = host.Client();
        using var other = host.Client();
        using var insecure = host.Client(https: false);
        await Problem(await insecure.GetAsync("/api/v1/auth/csrf"), HttpStatusCode.BadRequest);
        await Problem(await Post(insecure, "login", new { email = host.Email(), password = Password }, null), HttpStatusCode.BadRequest);
        var body = new { displayName = "Alex", email = host.Email(), password = Password };
        var response = await client.GetAsync("/api/v1/auth/csrf");
        var csrfCookie = response.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("__Host-TaskFlow.Csrf="));
        Assert.Contains("secure", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", csrfCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", csrfCookie, StringComparison.OrdinalIgnoreCase);
        await Problem(await Post(client, "register", body, null), HttpStatusCode.BadRequest);
        await Problem(await Post(client, "register", body, "invalid"), HttpStatusCode.BadRequest);
        await Problem(await Post(other, "register", body, await Csrf(client)), HttpStatusCode.BadRequest);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task DuplicateEmailsAndInvalidJsonAreRejectedAndPasswordsAreNotTrimmed()
    {
        await using var host = new AuthHost(fixture);
        using var client = host.Client();
        var token = await Csrf(client);
        var email = host.Email();
        await Problem(await Post(client, "register", new { displayName = " ", email, password = Password }, token), HttpStatusCode.BadRequest);
        await Problem(await Post(client, "register", new { displayName = "Alex", email, password = "weak" }, token), HttpStatusCode.BadRequest);
        using var duplicateBody = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/register")
        {
            Content = new StringContent("{\"email\":\"one@example.test\",\"Email\":\"two@example.test\"}", Encoding.UTF8, "application/json")
        };
        duplicateBody.Headers.Add("X-CSRF-TOKEN", token);
        await Problem(await client.SendAsync(duplicateBody), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Created, (await Post(client, "register", new { displayName = "Alex", email, password = $" {Password} " }, token)).StatusCode);
        await Problem(await Post(client, "register", new { displayName = "Alex", email = email.ToUpperInvariant(), password = Password }, token), HttpStatusCode.Conflict);
        await Problem(await Post(client, "login", new { email, password = Password }, token), HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = $" {Password} " }, token)).StatusCode);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task SharedAuthRateLimitCannotBeBypassedByForwardedIpHeaders()
    {
        await using var host = new AuthHost(fixture);
        using var client = host.Client();
        for (var attempt = 0; attempt < 11; attempt++)
        {
            client.DefaultRequestHeaders.Remove("X-Forwarded-For");
            client.DefaultRequestHeaders.Add("X-Forwarded-For", $"192.0.2.{attempt + 1}");
            var response = await Post(client, attempt % 2 == 0 ? "register" : "login", new { }, null);
            await Problem(response, attempt < 10 ? HttpStatusCode.BadRequest : HttpStatusCode.TooManyRequests);
            if (attempt == 10) Assert.NotNull(response.Headers.RetryAfter);
        }
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task LogoutDoesNotReportSuccessWhenSecurityStampCannotBeSaved()
    {
        var failure = new FailStampUpdate();
        await using var host = new AuthHost(fixture, failure);
        using var client = host.Client();
        var email = await Register(host, client);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode);
        var token = await Csrf(client);
        failure.Enabled = true;
        await Problem(await Post(client, "logout", null, token), HttpStatusCode.InternalServerError);
        failure.Enabled = false;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }

    private static async Task<string> Register(AuthHost host, HttpClient client)
    {
        var email = host.Email();
        Assert.Equal(HttpStatusCode.Created, (await Post(client, "register", new { displayName = "Alex", email, password = Password }, await Csrf(client))).StatusCode);
        return email;
    }

    private static async Task<string> Csrf(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf")).GetProperty("requestToken").GetString()!;

    private static Task<HttpResponseMessage> Post(HttpClient client, string path, object? body, string? token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/auth/{path}");
        if (body is not null) request.Content = JsonContent.Create(body);
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("traceId").GetString()));
        Assert.DoesNotContain("stackTrace", body.ToString());
        return body;
    }

    private sealed class AuthHost : IAsyncDisposable
    {
        private readonly MySqlFixture fixture;
        private readonly HashSet<string> emails = [];
        private readonly WebApplicationFactory<Program> factory;
        public AuthClock Clock { get; } = new();

        public AuthHost(MySqlFixture fixture, FailStampUpdate? failure = null, bool requireHttps = true, string environment = "Testing")
        {
            this.fixture = fixture;
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(environment);
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["ConnectionStrings:TaskFlow"] = fixture.ConnectionString, ["Transport:RequireHttps"] = requireHttps.ToString() }));
                builder.ConfigureTestServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<TimeProvider>(Clock));
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    services.AddControllers().AddApplicationPart(typeof(AuthProbeController).Assembly);
                    if (failure is not null) services.AddDbContext<TaskFlowDbContext>(options => options.AddInterceptors(failure));
                });
            });
        }

        public string Email()
        {
            var email = $"auth-{Guid.NewGuid():N}@example.test";
            emails.Add(email);
            return email;
        }

        public HttpClient Client(bool cookies = true, bool https = true) => factory.CreateClient(new()
        {
            BaseAddress = new Uri(https ? "https://localhost" : "http://localhost"),
            HandleCookies = cookies, AllowAutoRedirect = false
        });

        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync();
            // HTTP requests span scopes, so only this host's synthetic accounts are cleaned up.
            await using var context = fixture.CreateContext();
            await context.Users.Where(user => emails.Contains(user.Email!)).ExecuteDeleteAsync();
        }
    }

    public sealed class AuthClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FailStampUpdate : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<ApplicationUser>().Any(entry => entry.State == EntityState.Modified))
                throw new DbUpdateConcurrencyException("Synthetic stamp write failure.");
            return ValueTask.FromResult(result);
        }
    }
}

[ApiController, Route("_tests")]
public sealed class AuthProbeController : ControllerBase
{
    [HttpGet("protected")]
    public IActionResult Protected() => Ok();

    [HttpGet("forbidden"), Authorize(Roles = "NotAssigned")]
    public IActionResult Forbidden() => Ok();
}
