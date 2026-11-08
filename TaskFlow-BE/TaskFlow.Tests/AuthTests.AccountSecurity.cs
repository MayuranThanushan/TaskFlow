using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Email;
using TaskFlow.Infrastructure.Email;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public partial class AuthTests
{
    private const string NewPassword = "Changed-Secure-Password456!";
    private async Task<EmailRequest> Queued(AuthHost host, string email)
    {
        await using var db = fixture.CreateContext(); var user = await db.Users.SingleAsync(u => u.Email == email);
        var job = await db.AccountEmailJobs.OrderByDescending(j => j.Id).FirstAsync(j => j.UserId == user.Id);
        Assert.DoesNotContain(email, job.ProtectedPayload!); Assert.DoesNotContain("SecurityToken", job.ProtectedPayload!);
        var protector = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("TaskFlow.AccountMail.v1", user.Id.ToString(), job.MessageId);
        return JsonSerializer.Deserialize<EmailRequest>(protector.Unprotect(job.ProtectedPayload!))!;
    }
    [MySqlFact]
    public async Task VerificationQueuesEncryptedMailAndConcurrentConfirmationIsSingleUse()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var client = host.Client();
        var email = await Register(host, client); var csrf = await Csrf(client);
        var queued = await Post(client, "request-verification", new { email }, csrf); Assert.Equal(HttpStatusCode.Accepted, queued.StatusCode); Assert.Equal("", await queued.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, "request-verification", new { email = "missing-" + email }, csrf)).StatusCode);
        var mail = await Queued(host, email); Assert.Equal(EmailTemplate.EmailVerification, mail.Template); Assert.NotNull(mail.AccountUserId); Assert.Equal(0, mail.WorkspaceId);
        using var second = host.Client(); var secondCsrf = await Csrf(second);
        var results = await Task.WhenAll(Post(client, "confirm-email", new { userId = mail.AccountUserId.ToString(), token = mail.SecurityToken }, csrf), Post(second, "confirm-email", new { userId = mail.AccountUserId.ToString(), token = mail.SecurityToken }, secondCsrf));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.NoContent); Assert.Single(results, r => r.StatusCode == HttpStatusCode.BadRequest);
        await using var db = fixture.CreateContext(); Assert.True((await db.Users.SingleAsync(u => u.Email == email)).EmailConfirmed);
        Assert.All(await db.AccountEmailJobs.Where(j => j.UserId == mail.AccountUserId).ToListAsync(), j => { Assert.Equal("Cancelled", j.State); Assert.Null(j.ProtectedPayload); });
    }
    [MySqlFact]
    public async Task VerifiedAccountRecoveryRevokesEverySessionAndRejectsTokenReplay()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var first = host.Client(); using var second = host.Client(); using var recovery = host.Client();
        var email = await Register(host, first);
        await using (var db = fixture.CreateContext()) { var user = await db.Users.SingleAsync(u => u.Email == email); user.EmailConfirmed = true; await db.SaveChangesAsync(); }
        foreach (var client in new[] { first, second }) Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(recovery, "forgot-password", new { email }, await Csrf(recovery))).StatusCode);
        var mail = await Queued(host, email); var body = new { userId = mail.AccountUserId.ToString(), token = mail.SecurityToken, newPassword = NewPassword };
        Assert.Equal(HttpStatusCode.NoContent, (await Post(recovery, "reset-password", body, await Csrf(recovery))).StatusCode);
        foreach (var client in new[] { first, second }) await Problem(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
        await Problem(await Post(recovery, "reset-password", body, await Csrf(recovery)), HttpStatusCode.BadRequest);
        await Problem(await Post(recovery, "login", new { email, password = Password }, await Csrf(recovery)), HttpStatusCode.Unauthorized);
        Assert.Equal(HttpStatusCode.OK, (await Post(recovery, "login", new { email, password = NewPassword }, await Csrf(recovery))).StatusCode);
    }
    [MySqlFact]
    public async Task TokensArePurposeAccountAndTimeBoundAndApiNeverReturnsThem()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var client = host.Client(); var email = await Register(host, client); var csrf = await Csrf(client);
        Assert.Equal(HttpStatusCode.Accepted, (await Post(client, "request-verification", new { email }, csrf)).StatusCode); var mail = await Queued(host, email);
        await Problem(await Post(client, "reset-password", new { userId = mail.AccountUserId.ToString(), token = mail.SecurityToken, newPassword = NewPassword }, csrf), HttpStatusCode.BadRequest);
        await Problem(await Post(client, "confirm-email", new { userId = (mail.AccountUserId + 1).ToString(), token = mail.SecurityToken }, csrf), HttpStatusCode.BadRequest);
        host.Clock.Now = host.Clock.Now.AddMinutes(61);
        await Problem(await Post(client, "confirm-email", new { userId = mail.AccountUserId.ToString(), token = mail.SecurityToken }, csrf), HttpStatusCode.BadRequest);
        var capabilities = await client.GetStringAsync("/api/v1/auth/capabilities"); Assert.DoesNotContain(mail.SecurityToken, capabilities); Assert.DoesNotContain(email, capabilities);
    }
    [MySqlFact]
    public async Task RecoveryIsGenericForUnknownUnverifiedAndCoolingDownAccounts()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var client = host.Client(); var email = await Register(host, client); var csrf = await Csrf(client);
        foreach (var value in new[] { email, "missing-" + email })
        { var response = await Post(client, "forgot-password", new { email = value }, csrf); Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.Equal("", await response.Content.ReadAsStringAsync()); }
        await using var db = fixture.CreateContext(); var user = await db.Users.SingleAsync(u => u.Email == email); Assert.False(await db.AccountEmailJobs.AnyAsync(j => j.UserId == user.Id));
        foreach (var _ in Enumerable.Range(0, 3)) Assert.Equal(HttpStatusCode.Accepted, (await Post(client, "request-verification", new { email }, csrf)).StatusCode);
        Assert.Equal(1, await db.AccountEmailJobs.CountAsync(j => j.UserId == user.Id));
    }
    [MySqlFact]
    public async Task PasswordChangeRequiresCurrentProofAndInvalidatesOtherDevicesImmediately()
    {
        await using var host = new AuthHost(fixture); using var first = host.Client(); using var second = host.Client(); var email = await Register(host, first);
        foreach (var client in new[] { first, second }) await Post(client, "login", new { email, password = Password }, await Csrf(client));
        await Problem(await Post(first, "change-password", new { currentPassword = "Wrong", newPassword = NewPassword }, await Csrf(first)), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(first, "change-password", new { currentPassword = Password, newPassword = NewPassword }, await Csrf(first))).StatusCode);
        foreach (var client in new[] { first, second }) await Problem(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
    }
    [MySqlFact]
    public async Task ReauthenticationIsPasswordProvenSessionBoundAndExpiresInFiveMinutes()
    {
        await using var host = new AuthHost(fixture); using var client = host.Client(); var email = await Register(host, client);
        await Post(client, "login", new { email, password = Password }, await Csrf(client));
        Assert.Equal("null", (await client.GetStringAsync("/api/v1/auth/security")).Contains("reauthenticatedUntilUtc\":null") ? "null" : "unexpected");
        await Problem(await Post(client, "reauthenticate", new { currentPassword = "Wrong" }, await Csrf(client)), HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "reauthenticate", new { currentPassword = Password }, await Csrf(client))).StatusCode);
        var state = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/security"); Assert.Equal(JsonValueKind.String, state.GetProperty("reauthenticatedUntilUtc").ValueKind);
        host.Clock.Now = host.Clock.Now.AddMinutes(6); state = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/security"); Assert.Equal(JsonValueKind.Null, state.GetProperty("reauthenticatedUntilUtc").ValueKind);
    }
    [MySqlFact]
    public async Task EmailPreviewDefaultsOffAndAllNewMutationsRequireCsrf()
    {
        await using var host = new AuthHost(fixture); using var client = host.Client(); var csrf = await Csrf(client);
        Assert.Contains("false", await client.GetStringAsync("/api/v1/auth/capabilities"));
        await Problem(await Post(client, "forgot-password", new { email = "any@example.test" }, csrf), HttpStatusCode.ServiceUnavailable);
        await Problem(await Post(client, "request-verification", new { email = "any@example.test" }, null), HttpStatusCode.BadRequest);
        await Problem(await Post(client, "change-password", new { currentPassword = Password, newPassword = NewPassword }, csrf), HttpStatusCode.Unauthorized);
    }
    [MySqlFact]
    public async Task OptionalVerificationGatePreventsUnconfirmedLoginWithoutChangingDefault()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true, requireConfirmed: true); using var client = host.Client(); var email = await Register(host, client); var csrf = await Csrf(client);
        await Problem(await Post(client, "login", new { email, password = Password }, csrf), HttpStatusCode.Unauthorized);
        await Post(client, "request-verification", new { email }, csrf); var mail = await Queued(host, email);
        Assert.Equal(HttpStatusCode.NoContent, (await Post(client, "confirm-email", new { userId = mail.AccountUserId.ToString(), token = mail.SecurityToken }, csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Post(client, "login", new { email, password = Password }, await Csrf(client))).StatusCode);
    }
    private sealed class CapturingSender : IEmailSender
    {
        public int Calls { get; private set; }
        public Task<EmailResult> SendAsync(EmailRequest request, CancellationToken token = default) { Calls++; return Task.FromResult(new EmailResult(EmailStatus.Captured, "email.captured")); }
    }
    [MySqlFact]
    public async Task AccountWorkerCapturesOnlyProtectedRecipientJobsAndErasesTerminalPayload()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var client = host.Client(); var email = await Register(host, client);
        await Post(client, "request-verification", new { email }, await Csrf(client));
        var sender = new CapturingSender(); await using var db = fixture.CreateContext(); var worker = new AccountMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender);
        Assert.Equal(1, await worker.RunAsync()); Assert.Equal(1, sender.Calls); Assert.Equal(0, await worker.RunAsync());
        var user = await db.Users.SingleAsync(u => u.Email == email); var job = await db.AccountEmailJobs.SingleAsync(j => j.UserId == user.Id); Assert.Equal("Captured", job.State); Assert.Null(job.ProtectedPayload); Assert.Equal("email.captured", job.LastCode);
    }
    private sealed class FailingMail : IEmailSender
    {
        public Task<EmailResult> SendAsync(EmailRequest request, CancellationToken token = default) => throw new InvalidOperationException("PRIVATE_PROVIDER_CANARY");
    }
    [MySqlFact]
    public async Task AccountMailRetriesAreBoundedAndProviderFailuresNeverPersist()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var client = host.Client(); var email = await Register(host, client);
        await Post(client, "request-verification", new { email }, await Csrf(client)); await using var db = fixture.CreateContext(); var user = await db.Users.SingleAsync(u => u.Email == email);
        var worker = new AccountMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), new FailingMail());
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            Assert.Equal(1, await worker.RunAsync(1));
            db.ChangeTracker.Clear(); var job = await db.AccountEmailJobs.SingleAsync(j => j.UserId == user.Id); Assert.Equal(attempt, job.Attempts); Assert.DoesNotContain("PRIVATE", job.LastCode!);
            if (attempt < 5) { Assert.Equal("Pending", job.State); Assert.True(job.AvailableAtUtc > DateTime.UtcNow.AddSeconds(-1)); await db.AccountEmailJobs.Where(j => j.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAtUtc, DateTime.UtcNow.AddSeconds(-1))); }
            else { Assert.Equal("DeadLetter", job.State); Assert.Null(job.ProtectedPayload); }
        }
    }
    [MySqlFact]
    public async Task ExpiredOrCorruptAccountMailNeverReachesTheSender()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var client = host.Client(); var email = await Register(host, client);
        await Post(client, "request-verification", new { email }, await Csrf(client)); await using var db = fixture.CreateContext(); var user = await db.Users.SingleAsync(u => u.Email == email);
        await db.AccountEmailJobs.Where(j => j.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.ProtectedPayload, "not-valid-protection"));
        var sender = new CapturingSender(); var worker = new AccountMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender);
        Assert.Equal(1, await worker.RunAsync()); Assert.Equal(0, sender.Calls); db.ChangeTracker.Clear(); var job = await db.AccountEmailJobs.SingleAsync(j => j.UserId == user.Id); Assert.Equal("DeadLetter", job.State); Assert.Null(job.ProtectedPayload);
        await db.AccountEmailJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.State, "Pending").SetProperty(j => j.ExpiresAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        Assert.Equal(1, await worker.RunAsync()); Assert.Equal(0, sender.Calls); db.ChangeTracker.Clear(); Assert.Equal("Cancelled", (await db.AccountEmailJobs.SingleAsync(j => j.Id == job.Id)).State);
    }
    [MySqlFact]
    public async Task CompetingAccountWorkersOnlyClaimOneCopy()
    {
        await using var host = new AuthHost(fixture, environment: "Development", emailFlows: true); using var client = host.Client(); var email = await Register(host, client);
        await Post(client, "request-verification", new { email }, await Csrf(client)); var sender = new CapturingSender(); var protection = host.Services.GetRequiredService<IDataProtectionProvider>();
        await using var a = fixture.CreateContext(); await using var b = fixture.CreateContext();
        var counts = await Task.WhenAll(new AccountMailCapture(a, protection, sender).RunAsync(), new AccountMailCapture(b, protection, sender).RunAsync());
        Assert.Equal(1, counts.Sum()); Assert.Equal(1, sender.Calls);
    }

}
