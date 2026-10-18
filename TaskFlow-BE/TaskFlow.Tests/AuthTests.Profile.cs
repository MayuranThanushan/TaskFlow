using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;

public partial class AuthTests
{
    private static async Task<HttpResponseMessage> PatchProfile(HttpClient client, string json, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/users/me") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Add("X-CSRF-TOKEN", token);
        return await client.SendAsync(request);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ProfileUpdatesOnlyCurrentNameAndPreservesCredentialsSessionsAndNoOpTimestamp()
    {
        await using var host = new AuthHost(fixture);
        using var client = host.Client(); using var second = host.Client(); using var other = host.Client();
        var email = await Register(host, client); var otherEmail = await Register(host, other);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(second, "login", new { email, password = Password }, await Csrf(second))).StatusCode);
        await using var beforeDb = fixture.CreateContext();
        var before = await beforeDb.Users.AsNoTracking().SingleAsync(u => u.Email == email);
        var token = await Csrf(client);
        var response = await PatchProfile(client, JsonSerializer.Serialize(new { displayName = "  <Alex & Team>  " }), token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith("__Host-TaskFlow.Auth=")));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, body.EnumerateObject().Count()); Assert.Equal(before.Id.ToString(), body.GetProperty("id").GetString());
        Assert.Equal("<Alex & Team>", body.GetProperty("displayName").GetString()); Assert.Equal(email, body.GetProperty("email").GetString());
        Assert.Equal("<Alex & Team>", (await second.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("displayName").GetString());
        await using var afterDb = fixture.CreateContext(); var after = await afterDb.Users.AsNoTracking().SingleAsync(u => u.Id == before.Id);
        Assert.Equal(before.Email, after.Email); Assert.Equal(before.UserName, after.UserName); Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp); Assert.Equal(before.CreatedAtUtc, after.CreatedAtUtc); Assert.True(after.UpdatedAtUtc >= before.UpdatedAtUtc);
        Assert.Equal(HttpStatusCode.OK, (await PatchProfile(client, "{\"displayName\":\"<Alex & Team>\"}", token)).StatusCode);
        await using var noOpDb = fixture.CreateContext(); Assert.Equal(after.UpdatedAtUtc, (await noOpDb.Users.SingleAsync(u => u.Id == before.Id)).UpdatedAtUtc);
        Assert.Equal("Alex", (await noOpDb.Users.SingleAsync(u => u.Email == otherEmail)).DisplayName);
        Assert.Equal(HttpStatusCode.OK, (await Post(other, "login", new { email, password = Password }, await Csrf(other))).StatusCode);
        host.Clock.Now = host.Clock.Now.AddHours(8).AddSeconds(1);
        await Problem(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ProfileRejectsAnonymousCsrfInvalidNamesAndMassAssignmentWithoutChanges()
    {
        await using var host = new AuthHost(fixture); using var client = host.Client();
        await Problem(await PatchProfile(client, "{\"displayName\":\"New\"}", await Csrf(client)), HttpStatusCode.Unauthorized);
        var email = await Register(host, client);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode);
        await Problem(await PatchProfile(client, "{\"displayName\":\"New\"}", null), HttpStatusCode.BadRequest);
        var token = await Csrf(client);
        foreach (var json in new[] { "{}", "null", "{\"displayName\":null}", "{\"displayName\":123}", "{\"displayName\":\"   \"}", JsonSerializer.Serialize(new { displayName = new string('x', 101) }), "{\"displayName\":\"One\",\"DisplayName\":\"Two\"}" })
            await Problem(await PatchProfile(client, json, token), HttpStatusCode.BadRequest);
        foreach (var key in new[] { "id", "userId", "email", "role", "roles", "password", "securityStamp", "isAdmin" })
            await Problem(await PatchProfile(client, JsonSerializer.Serialize(new Dictionary<string, string> { ["displayName"] = "Changed", [key] = "injected" }), token), HttpStatusCode.BadRequest);
        Assert.Equal("Alex", (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("displayName").GetString());
        Assert.Equal(HttpStatusCode.OK, (await PatchProfile(client, JsonSerializer.Serialize(new { displayName = new string('x', 100) }), token)).StatusCode);
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ProfileConcurrencyConflictDoesNotPublishUnpersistedName()
    {
        var failure = new FailStampUpdate(); await using var host = new AuthHost(fixture, failure); using var client = host.Client();
        var email = await Register(host, client);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode);
        var token = await Csrf(client); failure.Enabled = true;
        await Problem(await PatchProfile(client, "{\"displayName\":\"Changed\"}", token), HttpStatusCode.Conflict);
        Assert.Equal("Alex", (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/me")).GetProperty("displayName").GetString());
    }
}
