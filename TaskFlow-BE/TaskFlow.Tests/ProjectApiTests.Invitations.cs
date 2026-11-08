using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Email;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Email;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private async Task VerifyInviteUsers(ProjectHost host)
    { await using var db = fixture.CreateContext(); var ids = host.Users.Select(u => u.Id).ToArray(); await db.Users.Where(u => ids.Contains(u.Id)).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, true)); }
    private async Task<EmailRequest> InviteMail(ProjectHost host, string id)
    {
        await using var db = fixture.CreateContext(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == long.Parse(id));
        var text = host.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("TaskFlow.InvitationMail.v1", row.WorkspaceId.ToString(), row.Id.ToString(), row.MessageId).Unprotect(row.ProtectedMessage!);
        var mail = JsonSerializer.Deserialize<EmailRequest>(text)!; Assert.NotEqual(mail.InvitationToken, row.TokenHash); Assert.DoesNotContain(mail.InvitationToken, row.ProtectedMessage!); return mail;
    }
    private async Task<JsonElement> Invite(ProjectHost host, HttpClient owner, Companies c, string email, string role = "Member", long? project = null)
    { var response = await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations", new { email, role, projectId = project?.ToString() }); Assert.Equal(HttpStatusCode.Created, response.StatusCode); return await Body(response); }
    [MySqlFact]
    public async Task InvitationCompanyFlowRequiresVerifiedIntendedEmailAndCommitsExactlyOnce()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host);
        using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(1); using var second = await host.LoginAsync(1); using var wrong = await host.LoginAsync(2);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = invite.GetProperty("id").GetString()!; var mail = await InviteMail(host, id); var path = W(c.A) + "/invitations/" + id;
        Assert.DoesNotContain(mail.InvitationToken, invite.ToString()); Assert.DoesNotContain("tokenHash", invite.ToString());
        await Problem(await recipient.GetAsync(W(c.A)), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(wrong, HttpMethod.Post, path + "/accept", new { token = mail.InvitationToken }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.B) + "/invitations/" + id + "/accept", new { token = mail.InvitationToken }), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/preview", new { token = mail.InvitationToken })).StatusCode);
        var results = await Task.WhenAll(host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/accept", new { token = mail.InvitationToken }), host.MemberWriteAsync(second, HttpMethod.Post, path + "/accept", new { token = mail.InvitationToken }));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK); Assert.Single(results, r => r.StatusCode == HttpStatusCode.NotFound);
        await using var db = fixture.CreateContext(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == long.Parse(id)); Assert.Equal("Accepted", row.State); Assert.Null(row.TokenHash); Assert.Null(row.PendingEmail); Assert.Null(row.ProtectedMessage);
        Assert.Equal(WorkspaceRole.Member, (await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[1].Id)).Role);
        Assert.Single(await db.InvitationTransitions.Where(a => a.InvitationId == row.Id && a.Action == "Accepted").ToArrayAsync());
        await Problem(await recipient.GetAsync(W(c.A) + "/projects/" + c.A1.Id), HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task InvitationProjectScopeNeverGrantsCompanyAdministrationOrPrivateProjectOverride()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host);
        using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(1); using var admin = await host.LoginAsync(2);
        await Problem(await host.MemberWriteAsync(admin, HttpMethod.Post, W(c.A) + "/invitations", new { email = host.Users[1].Email, role = "Viewer", projectId = c.A1.Id.ToString() }), HttpStatusCode.NotFound);
        var invite = await Invite(host, owner, c, host.Users[1].Email!, "Viewer", c.A1.Id); var id = invite.GetProperty("id").GetString()!; var mail = await InviteMail(host, id);
        await Problem(await admin.GetAsync(W(c.A) + "/invitations?projectId=" + c.A1.Id), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.OK, (await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + "/invitations/" + id + "/accept", new { token = mail.InvitationToken })).StatusCode);
        await using var db = fixture.CreateContext(); Assert.Equal(WorkspaceRole.Guest, (await db.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[1].Id)).Role);
        Assert.Equal(MembershipRole.Viewer, (await db.ProjectMembers.SingleAsync(m => m.ProjectId == c.A1.Id && m.UserId == host.Users[1].Id)).Role);
        Assert.Equal(HttpStatusCode.OK, (await recipient.GetAsync(W(c.A) + "/projects/" + c.A1.Id)).StatusCode);
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + "/projects/" + c.A1.Id + "/tasks", new { title = "Denied" }), HttpStatusCode.Forbidden);
        await db.WorkspaceMemberships.Where(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[2].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, WorkspaceRole.Owner));
        await db.WorkspaceMemberships.Where(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[0].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, WorkspaceRole.Member));
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations", new { email = host.Users[2].Email, role = "Editor", projectId = c.A1.Id.ToString() }), HttpStatusCode.Forbidden);
    }
    [MySqlFact]
    public async Task InvitationResendRotatesTheTokenAndRequiresRevisionAndCooldown()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(1);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = invite.GetProperty("id").GetString()!; var first = await InviteMail(host, id); var path = W(c.A) + "/invitations/" + id;
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/resend", new { expectedVersion = "1" }), HttpStatusCode.TooManyRequests);
        await using (var db = fixture.CreateContext()) await db.WorkspaceInvitations.Where(i => i.Id == long.Parse(id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.LastSentAtUtc, DateTime.UtcNow.AddMinutes(-2)));
        var resent = await Body(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/resend", new { expectedVersion = "1" })); Assert.Equal("2", resent.GetProperty("version").GetString()); var second = await InviteMail(host, id); Assert.NotEqual(first.InvitationToken, second.InvitationToken);
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/accept", new { token = first.InvitationToken }), HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/revoke", new { expectedVersion = "1" }), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Post, path + "/revoke", new { expectedVersion = "2" })).StatusCode);
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, path + "/accept", new { token = second.InvitationToken }), HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task InvitationExpiryAndInviterSuspensionDenyAcceptanceAndReleaseReservations()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(1);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = invite.GetProperty("id").GetString()!; var first = await InviteMail(host, id);
        await using (var db = fixture.CreateContext()) await db.WorkspaceInvitations.Where(i => i.Id == long.Parse(id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAtUtc, DateTime.UtcNow.AddSeconds(-1)));
        var list = await Body(await owner.GetAsync(W(c.A) + "/invitations")); Assert.Equal("Expired", list.GetProperty("items")[0].GetProperty("state").GetString());
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + "/invitations/" + id + "/accept", new { token = first.InvitationToken }), HttpStatusCode.Conflict);
        var next = await Invite(host, owner, c, host.Users[1].Email!); var nextId = next.GetProperty("id").GetString()!; var mail = await InviteMail(host, nextId);
        await using (var db = fixture.CreateContext()) await db.WorkspaceMemberships.Where(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[0].Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.State, WorkspaceMembershipState.Suspended));
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + "/invitations/" + nextId + "/accept", new { token = mail.InvitationToken }), HttpStatusCode.Conflict);
        await using var verify = fixture.CreateContext(); Assert.False(await verify.WorkspaceMemberships.AnyAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[1].Id));
    }
    [MySqlFact]
    public async Task InvitationBlocksDuplicatesUnverifiedRecipientsAndPrivilegeEscalation()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(1);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations", new { email = host.Users[1].Email, role = "Owner" }), HttpStatusCode.BadRequest);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = invite.GetProperty("id").GetString()!; var mail = await InviteMail(host, id);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations", new { email = host.Users[1].Email, role = "Member" }), HttpStatusCode.Conflict);
        await using (var db = fixture.CreateContext()) await db.Users.Where(u => u.Id == host.Users[1].Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailConfirmed, false));
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + "/invitations/" + id + "/accept", new { token = mail.InvitationToken }), HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task InvitationReservesTheLastSeatWithoutBlockingItsAcceptance()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(1);
        await using (var db = fixture.CreateContext())
        {
            for (var n = 0; n < 22; n++) { var email = "seat-" + Guid.NewGuid().ToString("N") + "@example.test"; var user = new ApplicationUser { DisplayName = "Synthetic seat", Email = email, NormalizedEmail = email.ToUpperInvariant(), UserName = email, NormalizedUserName = email.ToUpperInvariant() }; db.Users.Add(user); await db.SaveChangesAsync(); host.Users.Add(user); db.WorkspaceMemberships.Add(new() { WorkspaceId = c.A.Id, UserId = user.Id, Role = WorkspaceRole.Member }); } await db.SaveChangesAsync();
        }
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = invite.GetProperty("id").GetString()!; var mail = await InviteMail(host, id);
        var policy = (await Body(await owner.GetAsync(W(c.A) + "/policy"))).GetProperty("capacity"); Assert.Equal(1, policy.GetProperty("reservedMemberSeats").GetInt32()); Assert.Equal(24, policy.GetProperty("activeMembers").GetInt32());
        Assert.EndsWith("Z", invite.GetProperty("resendAvailableAtUtc").GetString());
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations", new { email = "excess@example.test", role = "Member" }), HttpStatusCode.Conflict);
        Assert.Equal(HttpStatusCode.OK, (await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + "/invitations/" + id + "/accept", new { token = mail.InvitationToken })).StatusCode);
        await using var verify = fixture.CreateContext(); Assert.Equal(25, await verify.WorkspaceMemberships.CountAsync(m => m.WorkspaceId == c.A.Id && m.State == WorkspaceMembershipState.Active));
    }
    private sealed class InviteSender : IEmailSender
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<EmailResult> SendAsync(EmailRequest request, CancellationToken token = default) { Started.TrySetResult(); await Released.Task.WaitAsync(token); return new(EmailStatus.Captured, "email.captured"); }
    }
    [MySqlFact]
    public async Task InvitationRevokeDuringCaptureCannotReviveTheGrantOrDelivery()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, host.Users[1].Email!); var id = invite.GetProperty("id").GetString()!;
        var sender = new InviteSender(); await using var db = fixture.CreateContext(); var task = new InvitationMailCapture(db, host.Services.GetRequiredService<IDataProtectionProvider>(), sender).RunAsync(); await sender.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.NoContent, (await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations/" + id + "/revoke", new { expectedVersion = "1" })).StatusCode);
        sender.Released.SetResult(); await task; db.ChangeTracker.Clear(); var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == long.Parse(id)); Assert.Equal("Revoked", row.State); Assert.Equal("Cancelled", row.DeliveryState); Assert.Null(row.ProtectedMessage);
    }
    [MySqlFact]
    public async Task InvitationFeatureIsDefaultOffAndCsrfIsRequired()
    {
        await using var host = new ProjectHost(fixture, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); using var owner = await host.LoginAsync(0); await Problem(await owner.GetAsync(W(c.A) + "/invitations"), HttpStatusCode.NotFound);
    }
    private sealed class FailInvitationJournal : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken token = default)
        { if (data.Context!.ChangeTracker.Entries<InvitationTransition>().Any(e => e.State == EntityState.Added)) throw new InvalidOperationException("Synthetic invitation journal failure"); return ValueTask.FromResult(result); }
    }
    [MySqlFact]
    public async Task InvitationAuditFailureRollsBackSourceDeliveryAndSeatReservation()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true, new FailInvitationJournal()); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        await Problem(await host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations", new { email = host.Users[1].Email, role = "Member" }), HttpStatusCode.InternalServerError);
        await using var db = fixture.CreateContext(); Assert.False(await db.WorkspaceInvitations.AnyAsync(i => i.WorkspaceId == c.A.Id)); Assert.False(await db.WorkspaceAuditLogs.AnyAsync(i => i.WorkspaceId == c.A.Id && i.Action == "InvitationCreated"));
    }
    [MySqlFact]
    public async Task InvitationCompositeProjectScopeAndAppendOnlyJournalAreDatabaseProtected()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0);
        var invite = await Invite(host, owner, c, host.Users[1].Email!, "Viewer", c.A1.Id); var id = long.Parse(invite.GetProperty("id").GetString()!);
        await using (var db = fixture.CreateContext()) { var row = await db.WorkspaceInvitations.SingleAsync(i => i.Id == id); row.ProjectId = c.B1.Id; await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); }
        await using (var db = fixture.CreateContext()) { var audit = await db.InvitationTransitions.FirstAsync(i => i.InvitationId == id); audit.Action = "tamper"; await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync()); }
    }
    [MySqlFact]
    public async Task InvitationDuplicateRaceHasOneWinnerAndAcceptanceCannotReactivateSuspension()
    {
        await using var host = new ProjectHost(fixture, true, true, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await VerifyInviteUsers(host); using var owner = await host.LoginAsync(0); using var other = await host.LoginAsync(0); using var recipient = await host.LoginAsync(1);
        var results = await Task.WhenAll(host.MemberWriteAsync(owner, HttpMethod.Post, W(c.A) + "/invitations", new { email = host.Users[1].Email, role = "Member" }), host.MemberWriteAsync(other, HttpMethod.Post, W(c.A) + "/invitations", new { email = host.Users[1].Email, role = "Member" }));
        var winner = Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created); Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict); var id = (await Body(winner)).GetProperty("id").GetString()!; var mail = await InviteMail(host, id);
        await using (var db = fixture.CreateContext()) { db.WorkspaceMemberships.Add(new() { WorkspaceId = c.A.Id, UserId = host.Users[1].Id, Role = WorkspaceRole.Member, State = WorkspaceMembershipState.Suspended }); await db.SaveChangesAsync(); }
        await Problem(await host.MemberWriteAsync(recipient, HttpMethod.Post, W(c.A) + "/invitations/" + id + "/accept", new { token = mail.InvitationToken }), HttpStatusCode.Conflict);
        await using var verify = fixture.CreateContext(); Assert.Equal(WorkspaceMembershipState.Suspended, (await verify.WorkspaceMemberships.SingleAsync(m => m.WorkspaceId == c.A.Id && m.UserId == host.Users[1].Id)).State);
    }

}
