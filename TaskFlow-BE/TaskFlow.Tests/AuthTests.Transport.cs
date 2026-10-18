using System.Net;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public partial class AuthTests
{
    [MySqlFact, Trait("Category", "MySql")]
    public async Task OptionalTransportSupportsSessionCsrfAndLogout()
    {
        foreach (var environment in new[] { "Local", "UAT", "Production" })
        foreach (var https in new[] { false, true })
        {
            await using var host = new AuthHost(fixture, requireHttps: false, environment: environment);
            using var client = host.Client(https: https);
            var bootstrap = await client.GetAsync("/api/v1/auth/csrf");
            Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
            var csrf = bootstrap.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("TaskFlow.Csrf="));
            Assert.Equal(https, csrf.Contains("; secure", StringComparison.OrdinalIgnoreCase));
            var email = await Register(host, client);
            // Missing CSRF must still fail in HTTP-compatible mode.
            await Problem(await Post(client, "login", new { email, password = Password }, null), HttpStatusCode.BadRequest);
            var login = await Post(client, "login", new { email, password = Password }, await Csrf(client));
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            var auth = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith("TaskFlow.Auth="));
            Assert.Equal(https, auth.Contains("; secure", StringComparison.OrdinalIgnoreCase));
            Assert.Contains("httponly", auth, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=lax", auth, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "logout", null, await Csrf(client))).StatusCode);
            await Problem(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
            using var replay = host.Client(cookies: false, https: https);
            replay.DefaultRequestHeaders.Add("Cookie", auth.Split(';')[0]);
            await Problem(await replay.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        }
    }
}
