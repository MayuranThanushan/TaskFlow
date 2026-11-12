using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Email;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Email;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

public sealed partial class ProjectApiTests
{
    [MySqlFact]
    public async Task OnboardingSendBudgetSerializesCreateAndResendAcrossAdministrators()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host);
        using var owner = await host.LoginAsync(0); using var admin = await host.LoginAsync(2); using var otherCompanyOwner = await host.LoginAsync(1);
        var first = await Invite(host, owner, c, "first@example.test"); var id = long.Parse(first.GetProperty("id").GetString()!);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IWorkspaceInvitations>();
            for (var n = 0; n < 18; n++) await service.CreateAsync(new(host.Users[0].Id, c.A.Id), $"budget-{n}@example.test", null, "Member", default);
        }
        await using (var db = fixture.CreateContext())
            await db.WorkspaceInvitations.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.LastSentAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var results = await Task.WhenAll(
            host.MemberWriteAsync(admin, HttpMethod.Post, W(c.A) + "/invitations", new { email = "last-seat@example.test", role = "Member" }),
            host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + $"/invitations/{id}/resend", new { expectedVersion = "1" }));
        Assert.Single(results, r => r.IsSuccessStatusCode);
        var blocked = Assert.Single(results, r => r.StatusCode == HttpStatusCode.TooManyRequests);
        Assert.Equal("invitation.send_limited", (await Problem(blocked, HttpStatusCode.TooManyRequests)).GetProperty("code").GetString());
        Assert.InRange(blocked.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 3600);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(20, await db.InvitationTransitions.CountAsync(t => t.WorkspaceId == c.A.Id && (t.Action == "Created" || t.Action == "Resent")));
            var current = await db.WorkspaceInvitations.SingleAsync(i => i.Id == id);
            Assert.Equal(results[1].IsSuccessStatusCode ? 2 : 1, current.Version);
            Assert.Equal(results[0].IsSuccessStatusCode, await db.WorkspaceInvitations.AnyAsync(i => i.WorkspaceId == c.A.Id && i.Email == "last-seat@example.test"));
        }
        // Revocation releases capacity, but does not erase mail-abuse history.
        var version = results[1].IsSuccessStatusCode ? "2" : "1";
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + $"/invitations/{id}/revoke", new { expectedVersion = version })).StatusCode);
        var rejected = await host.MemberWriteAsync(admin, HttpMethod.Post, W(c.A) + "/invitations", new { email = "still-blocked@example.test", role = "Guest" });
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.MemberWriteAsync(otherCompanyOwner, HttpMethod.Post, W(c.B) + "/invitations", new { email = "independent@example.test", role = "Member" })).StatusCode);
    }

    [MySqlFact]
    public async Task OnboardingBudgetIgnoresExpiredHistoryAndDeliveryAttempts()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, "window@example.test"); var id = long.Parse(invite.GetProperty("id").GetString()!);
        await using (var db = fixture.CreateContext())
        {
            for (var n = 0; n < 20; n++)
            {
                db.InvitationTransitions.Add(new() { WorkspaceId = c.A.Id, InvitationId = id, ActorUserId = host.Users[0].Id, Version = 1, Action = "Resent", CreatedAtUtc = DateTime.UtcNow.AddHours(-2) });
                db.InvitationTransitions.Add(new() { WorkspaceId = c.A.Id, InvitationId = id, ActorUserId = host.Users[0].Id, Version = 1, Action = "DeliveryAttempted", CreatedAtUtc = DateTime.UtcNow });
            }
            await db.SaveChangesAsync();
            await db.WorkspaceInvitations.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.LastSentAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        }
        Assert.Equal(HttpStatusCode.OK, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + $"/invitations/{id}/resend", new { expectedVersion = "1" })).StatusCode);
    }

    private sealed class OutageSender : IEmailSender
    {
        public List<string> MessageIds { get; } = [];
        public bool Recover { get; set; }
        public EmailStatus SuccessStatus { get; set; } = EmailStatus.Captured;
        public Task<EmailResult> SendAsync(EmailRequest request, CancellationToken token = default)
        {
            MessageIds.Add(request.MessageId);
            if (!Recover) throw new IOException("Synthetic outage; private recipient and token diagnostics must not escape");
            return Task.FromResult(new EmailResult(SuccessStatus, SuccessStatus == EmailStatus.ProviderAccepted ? "email.provider_accepted" : "email.captured"));
        }
    }

    [MySqlFact]
    public async Task OnboardingMailOutageRetriesWithStableIdWithoutGrantingAccess()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = long.Parse(invite.GetProperty("id").GetString()!);
        var sender = new OutageSender();
        await using (var db = fixture.CreateContext())
        {
            await new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender).RunAsync();
            db.ChangeTracker.Clear(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == id);
            Assert.Equal("Pending", row.DeliveryState); Assert.Equal("email.capture_unavailable", row.LastCode);
            Assert.Equal(1, row.Attempts); Assert.NotNull(row.ProtectedMessage); Assert.Null(row.LeaseToken);
            Assert.True(row.AvailableAtUtc > row.LastSentAtUtc);
            Assert.False(await db.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[1].Id));
            Assert.Empty(await db.InvitationTransitions.Where(t => t.InvitationId == id && t.Action == "Captured").ToArrayAsync());
            Assert.Equal(0, await new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender).RunAsync());
            await db.WorkspaceInvitations.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.AvailableAtUtc, DateTime.UtcNow.AddMinutes(-1)));
        }
        sender.Recover = true;
        await using (var db = fixture.CreateContext())
        {
            await new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender).RunAsync();
            db.ChangeTracker.Clear(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == id);
            Assert.Equal("Captured", row.DeliveryState); Assert.Equal(2, row.Attempts);
            Assert.Null(row.ProtectedMessage); Assert.Null(row.LeaseToken);
            Assert.False(await db.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[1].Id));
        }
        Assert.Equal(2, sender.MessageIds.Count); Assert.Single(sender.MessageIds.Distinct());
    }

    [MySqlFact]
    public async Task OnboardingInviterRemovalBeforeDispatchCancelsMailWithoutCallingSender()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = long.Parse(invite.GetProperty("id").GetString()!); var mail = await InviteMail(host, id.ToString());
        await using (var db = fixture.CreateContext()) await db.WorkspaceMemberships.Where(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[0].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.State, WorkspaceMembershipState.Removed));
        var sender = new OutageSender { Recover = true };
        await using (var db = fixture.CreateContext())
        {
            await new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender).RunAsync();
            db.ChangeTracker.Clear(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == id);
            Assert.Equal("Cancelled", row.DeliveryState); Assert.Null(row.ProtectedMessage); Assert.Empty(sender.MessageIds);
        }
        using var recipient = await host.LoginAsync(1);
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + $"/invitations/{id}/accept", new { token = mail.InvitationToken }), HttpStatusCode.Conflict);
    }

    [MySqlFact]
    public async Task OnboardingProviderAcceptanceIsTerminalWithoutClaimingRecipientDelivery()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = long.Parse(invite.GetProperty("id").GetString()!);
        var sender = new OutageSender { Recover = true, SuccessStatus = EmailStatus.ProviderAccepted };
        await using var db = fixture.CreateContext(); var pump = new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender);
        Assert.Equal(1, await pump.RunAsync()); db.ChangeTracker.Clear(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == id);
        Assert.Equal("ProviderAccepted", row.DeliveryState); Assert.Equal("Pending", row.State); Assert.Equal("email.provider_accepted", row.LastCode);
        Assert.Null(row.ProtectedMessage); Assert.Null(row.LeaseToken); Assert.NotNull(row.TokenHash);
        Assert.Single(await db.InvitationTransitions.Where(t => t.InvitationId == id && t.Action == "ProviderAccepted").ToArrayAsync());
        Assert.False(await db.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[1].Id));
        Assert.Equal(0, await pump.RunAsync()); Assert.Single(sender.MessageIds);
    }

    [MySqlFact]
    public async Task OnboardingAccountProviderAcceptanceDoesNotVerifyAccountOrSendTwice()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync();
        var protection = host.Services.GetRequiredService<IDataProtectionProvider>(); var user = host.Users[0];
        var job = new AccountEmailJob { UserId = user.Id, Kind = "EmailVerification", CreatedAtUtc = DateTime.UtcNow, AvailableAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddHours(1) };
        var request = new EmailRequest { AccountUserId = user.Id, MessageId = job.MessageId, Recipient = user.Email!, Template = EmailTemplate.EmailVerification, DisplayName = "Synthetic account", SecurityToken = new string('s', 64) };
        job.ProtectedPayload = protection.CreateProtector("TaskFlow.AccountMail.v1", user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), job.MessageId).Protect(JsonSerializer.Serialize(request));
        var sender = new OutageSender { Recover = true, SuccessStatus = EmailStatus.ProviderAccepted };
        await using var db = fixture.CreateContext(); db.AccountEmailJobs.Add(job); await db.SaveChangesAsync();
        var pump = new AccountMailCapture(db, protection, sender); Assert.Equal(1, await pump.RunAsync()); db.ChangeTracker.Clear();
        var row = await db.AccountEmailJobs.SingleAsync(j => j.Id == job.Id); Assert.Equal("ProviderAccepted", row.State); Assert.Null(row.ProtectedPayload); Assert.Null(row.LeaseToken);
        Assert.False(await db.Users.Where(u => u.Id == user.Id).Select(u => u.EmailConfirmed).SingleAsync());
        Assert.Equal(0, await pump.RunAsync()); Assert.Single(sender.MessageIds);
    }

    [MySqlFact]
    public async Task OnboardingPersistentMailOutageStopsAfterFiveAttemptsAndClearsPayload()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true);
        await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = long.Parse(invite.GetProperty("id").GetString()!); var sender = new OutageSender();
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await using var db = fixture.CreateContext(); await db.WorkspaceInvitations.Where(i => i.Id == id).ExecuteUpdateAsync(s => s.SetProperty(i => i.AvailableAtUtc, DateTime.UtcNow.AddMinutes(-1)));
            await new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender).RunAsync();
            db.ChangeTracker.Clear(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == id); Assert.Equal(attempt, row.Attempts);
            Assert.Equal(attempt == 5 ? "DeadLetter" : "Pending", row.DeliveryState);
            if (attempt == 5) Assert.Null(row.ProtectedMessage); else Assert.NotNull(row.ProtectedMessage);
        }
        Assert.Equal(5, sender.MessageIds.Count); Assert.Single(sender.MessageIds.Distinct());
    }
}
