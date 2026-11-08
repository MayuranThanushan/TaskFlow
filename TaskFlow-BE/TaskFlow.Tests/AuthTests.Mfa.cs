using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;

public partial class AuthTests
{
    private async Task<string> VerifiedMfaAccount(AuthHost host, HttpClient client)
    {
        var email = await Register(host, client);
        await using var db = fixture.CreateContext(); await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode); return email;
    }
    private static string Otp(AuthHost host, string key) => MfaCodes.At(key, host.Clock.Now.ToUnixTimeSeconds() / 30);
    private static async Task<(string Key, string[] Codes)> Enroll(AuthHost host, HttpClient client)
    {
        var setup = await Post(client, "mfa/setup", new { currentPassword = Password }, await Csrf(client)); Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        var confirm = await Post(client, "mfa/confirm", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(client)); Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        return (key, (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToArray());
    }
    [MySqlFact]
    public async Task MfaEnrollmentIsOptionalVerifiedSessionBoundAndPrivate()
    {
        await using var off = new AuthHost(fixture); using var a = off.Client(); await VerifiedMfaAccount(off, a);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(a, "mfa/setup", new { currentPassword = Password }, await Csrf(a))).StatusCode);
        await using var host = new AuthHost(fixture, mfa: true); using var client = host.Client(); var email = await Register(host, client);
        await Post(client, "login", new { email, password = Password }, await Csrf(client));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "mfa/setup", new { currentPassword = Password }, await Csrf(client))).StatusCode);
        await using var db = fixture.CreateContext(); await db.Users.Where(u => u.Email == email).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true));
        using var other = host.Client(); await Post(other, "login", new { email, password = Password }, await Csrf(other));
        var setup = await Post(client, "mfa/setup", new { currentPassword = Password }, await Csrf(client)); var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(other, "mfa/confirm", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(other))).StatusCode);
        var result = await Post(client, "mfa/confirm", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(client)); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode);
        var state = await db.AccountMfa.AsNoTracking().SingleAsync(m => m.UserId == db.Users.Where(u => u.Email == email).Select(u => u.Id).Single());
        var codes = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes"); Assert.Equal(10, codes.GetArrayLength());
        Assert.DoesNotContain(key, state.ProtectedSecret!); Assert.Null(state.PendingProtectedSecret);
        foreach (var code in codes.EnumerateArray()) Assert.DoesNotContain(code.GetString()!, state.RecoveryHashes);
        var publicState = await client.GetStringAsync("/api/v1/auth/mfa"); Assert.DoesNotContain(key, publicState); Assert.DoesNotContain("recoveryHashes", publicState);
        var events = await db.SecurityEvents.Where(e => e.SubjectUserId == state.UserId).ToArrayAsync();
        Assert.DoesNotContain(key, JsonSerializer.Serialize(events)); Assert.Contains(events, e => e.Action == "MfaEnabled");
    }
    [MySqlFact]
    public async Task MfaLoginRequiresBothFactorsAndRejectsTotpReplayAndConcurrentRecoveryReplay()
    {
        await using var host = new AuthHost(fixture, mfa: true); using var client = host.Client(); var email = await VerifiedMfaAccount(host, client); var (key, codes) = await Enroll(host, client);
        // Disabling enrollment must never turn an existing account into password-only sign-in.
        host.Services.GetRequiredService<IConfiguration>()["Mfa:Enabled"] = "false";
        using var first = host.Client(); using var second = host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(first, "login", new { email, password = Password }, await Csrf(first))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(first, "login", new { email, password = Password, code = Otp(host, key) }, await Csrf(first))).StatusCode);
        host.Clock.Now = host.Clock.Now.AddSeconds(30);
        Assert.Equal(HttpStatusCode.OK, (await Post(first, "login", new { email, password = Password, code = Otp(host, key) }, await Csrf(first))).StatusCode);
        var t1 = await Csrf(first); var t2 = await Csrf(second);
        var results = await Task.WhenAll(Post(first, "login", new { email, password = Password, recoveryCode = codes[0] }, t1), Post(second, "login", new { email, password = Password, recoveryCode = codes[0] }, t2));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Unauthorized);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/mfa"); Assert.Equal(9, state.GetProperty("recoveryCodesRemaining").GetInt32());
    }
    [MySqlFact]
    public async Task RecoverySessionCanRestoreAuthenticatorButCannotBypassAdminPolicyOrDisableRequiredMfa()
    {
        await using var host = new AuthHost(fixture, mfa: true, requireMfa: true); using var client = host.Client(); var email = await VerifiedMfaAccount(host, client);
        await using var db = fixture.CreateContext(); var user = await db.Users.SingleAsync(u => u.Email == email);
        var company = new Workspace { Name = "MFA test company", LegacyOwnerId = user.Id }; db.Workspaces.Add(company); await db.SaveChangesAsync();
        db.WorkspaceMemberships.Add(new() { WorkspaceId = company.Id, UserId = user.Id, Role = WorkspaceRole.Owner }); await db.SaveChangesAsync();
        async Task<HttpResponseMessage> CompanyWrite(HttpClient c) { var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v2/workspaces/{company.Id}/projects") { Content = JsonContent.Create(new { }) }; request.Headers.Add("X-CSRF-TOKEN", await Csrf(c)); return await c.SendAsync(request); }
        Assert.Equal(HttpStatusCode.Forbidden, (await CompanyWrite(client)).StatusCode);
        var (key, codes) = await Enroll(host, client);
        // Proof passed; this deliberately invalid body reaches ordinary validation, without creating data.
        Assert.Equal(HttpStatusCode.BadRequest, (await CompanyWrite(client)).StatusCode);
        host.Clock.Now = host.Clock.Now.AddMinutes(6); Assert.Equal(HttpStatusCode.Forbidden, (await CompanyWrite(client)).StatusCode);
        using var recovery = host.Client(); Assert.Equal(HttpStatusCode.OK, (await Post(recovery, "login", new { email, password = Password, recoveryCode = codes[0] }, await Csrf(recovery))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await CompanyWrite(recovery)).StatusCode);
        var setup = await Post(recovery, "mfa/setup", new { currentPassword = Password }, await Csrf(recovery)); Assert.Equal(HttpStatusCode.OK, setup.StatusCode);
        var replacement = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Post(recovery, "mfa/confirm", new { currentPassword = Password, code = Otp(host, replacement) }, await Csrf(recovery))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CompanyWrite(recovery)).StatusCode);
        var denied = await Post(recovery, "mfa/disable", new { currentPassword = Password, recoveryCode = codes[2] }, await Csrf(recovery));
        Assert.Equal("mfa.required_for_administration", (await denied.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
    }
    [MySqlFact]
    public async Task MfaRecoveryRotationInvalidatesOldCodesAndDisablingRevokesAllSessions()
    {
        await using var host = new AuthHost(fixture, mfa: true); using var client = host.Client(); var email = await VerifiedMfaAccount(host, client); var (key, old) = await Enroll(host, client);
        host.Clock.Now = host.Clock.Now.AddSeconds(30);
        var rotated = await Post(client, "mfa/recovery-codes", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(client)); Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);
        var codes = (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("recoveryCodes");
        using var other = host.Client(); Assert.Equal(HttpStatusCode.Unauthorized, (await Post(other, "login", new { email, password = Password, recoveryCode = old[0] }, await Csrf(other))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(other, "login", new { email, password = Password, recoveryCode = codes[0].GetString() }, await Csrf(other))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "mfa/disable", new { currentPassword = Password, recoveryCode = codes[1].GetString() }, await Csrf(client))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(other, "login", new { email, password = Password }, await Csrf(other))).StatusCode);
        await using var db = fixture.CreateContext(); var state = await db.AccountMfa.SingleAsync(m => m.UserId == db.Users.Where(u => u.Email == email).Select(u => u.Id).Single());
        Assert.Null(state.ProtectedSecret); Assert.Equal("[]", state.RecoveryHashes);
    }
    [MySqlFact]
    public async Task FailedMfaConfirmationRollsBackCredentialAndAuditTogether()
    {
        var failure = new FailStampUpdate(); await using var host = new AuthHost(fixture, failure, mfa: true); using var client = host.Client(); await VerifiedMfaAccount(host, client);
        var setup = await Post(client, "mfa/setup", new { currentPassword = Password }, await Csrf(client)); var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        var token = await Csrf(client); failure.Enabled = true;
        Assert.Equal(HttpStatusCode.InternalServerError, (await Post(client, "mfa/confirm", new { currentPassword = Password, code = Otp(host, key) }, token)).StatusCode);
        failure.Enabled = false;
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "mfa/confirm", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(client))).StatusCode);
    }
    [MySqlFact]
    public async Task MfaReauthenticationAndPasswordChangeRequireFreshAuthenticatorAndRetainMfa()
    {
        await using var host = new AuthHost(fixture, mfa: true); using var client = host.Client(); var email = await VerifiedMfaAccount(host, client); var (key, _) = await Enroll(host, client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client, "reauthenticate", new { currentPassword = Password }, await Csrf(client))).StatusCode);
        host.Clock.Now = host.Clock.Now.AddSeconds(30);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "reauthenticate", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(client))).StatusCode);
        host.Clock.Now = host.Clock.Now.AddSeconds(30); const string changed = "Changed-Example-Password123!";
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "change-password", new { currentPassword = Password, newPassword = changed, code = Otp(host, key) }, await Csrf(client))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(client, "login", new { email, password = changed }, await Csrf(client))).StatusCode);
        host.Clock.Now = host.Clock.Now.AddSeconds(30);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = changed, code = Otp(host, key) }, await Csrf(client))).StatusCode);
    }
    [MySqlFact]
    public async Task IncorrectSecondFactorsLockAccountEvenWithCorrectPassword()
    {
        await using var host = new AuthHost(fixture, mfa: true); using var client = host.Client(); var email = await VerifiedMfaAccount(host, client); var (key, _) = await Enroll(host, client);
        using var attacker = host.Client();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.Unauthorized, (await Post(attacker, "login", new { email, password = Password }, await Csrf(attacker))).StatusCode);
        host.Clock.Now = host.Clock.Now.AddSeconds(30);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(attacker, "login", new { email, password = Password, code = Otp(host, key) }, await Csrf(attacker))).StatusCode);
        await using var db = fixture.CreateContext(); Assert.NotNull((await db.Users.SingleAsync(u => u.Email == email)).LockoutEnd);
    }
    [MySqlFact]
    public async Task ExpiredEnrollmentDoesNotEnableMfaOrReturnRecoveryCodes()
    {
        await using var host = new AuthHost(fixture, mfa: true); using var client = host.Client(); await VerifiedMfaAccount(host, client);
        var setup = await Post(client, "mfa/setup", new { currentPassword = Password }, await Csrf(client)); var key = (await setup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        host.Clock.Now = host.Clock.Now.AddMinutes(11);
        var result = await Post(client, "mfa/confirm", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(client)); Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.DoesNotContain("recoveryCodes", await result.Content.ReadAsStringAsync());
        Assert.False((await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/mfa")).GetProperty("enabled").GetBoolean());
    }
    [MySqlFact]
    public async Task EmailPasswordRecoveryCannotBypassMfaAndConsumesRecoveryProofOnce()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true, mfa: true); using var client = host.Client(); var email = await VerifiedMfaAccount(host, client); var (_, codes) = await Enroll(host, client);
        using var recovery = host.Client(); Assert.Equal(HttpStatusCode.Accepted, (await Post(recovery, "forgot-password", new { email }, await Csrf(recovery))).StatusCode);
        var mail = await Queued(host, email);
        var reset = new { userId = mail.AccountUserId.ToString(), token = mail.SecurityToken, newPassword = NewPassword };
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(recovery, "reset-password", reset, await Csrf(recovery))).StatusCode);
        var proof = new { reset.userId, reset.token, reset.newPassword, recoveryCode = codes[0] };
        Assert.Equal(HttpStatusCode.NoContent, (await Post(recovery, "reset-password", proof, await Csrf(recovery))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(recovery, "reset-password", proof, await Csrf(recovery))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(recovery, "login", new { email, password = NewPassword }, await Csrf(recovery))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(recovery, "login", new { email, password = NewPassword, recoveryCode = codes[1] }, await Csrf(recovery))).StatusCode);
    }
    [MySqlFact]
    public async Task LastRecoveryCodePermitsSessionBoundRestorationWithoutAnotherCode()
    {
        await using var host = new AuthHost(fixture, mfa: true); using var client = host.Client(); var email = await VerifiedMfaAccount(host, client); var (_, codes) = await Enroll(host, client);
        await using var db = fixture.CreateContext(); var user = await db.Users.SingleAsync(u => u.Email == email);
        var hashes = JsonSerializer.Serialize(new[] { MfaCodes.RecoveryHash(user.Id, codes[0]) });
        await db.AccountMfa.Where(m => m.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.RecoveryHashes, hashes));
        using var recovery = host.Client(); Assert.Equal(HttpStatusCode.OK, (await Post(recovery, "login", new { email, password = Password, recoveryCode = codes[0] }, await Csrf(recovery))).StatusCode);
        var status = await recovery.GetFromJsonAsync<JsonElement>("/api/v1/auth/mfa"); Assert.Equal(0, status.GetProperty("recoveryCodesRemaining").GetInt32()); Assert.Equal(JsonValueKind.Null, status.GetProperty("verifiedUntilUtc").ValueKind); Assert.Equal(JsonValueKind.String, status.GetProperty("recoveryVerifiedUntilUtc").ValueKind);
        var result = await Post(recovery, "mfa/setup", new { currentPassword = Password }, await Csrf(recovery)); Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var key = (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sharedKey").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await Post(recovery, "mfa/confirm", new { currentPassword = Password, code = Otp(host, key) }, await Csrf(recovery))).StatusCode);
    }
}
