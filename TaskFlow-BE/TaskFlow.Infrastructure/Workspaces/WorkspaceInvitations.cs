using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Features.Workspaces;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Application.Features.Email;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Email;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Workspaces;
public sealed class WorkspaceInvitations(IServiceScopeFactory scopes, IDataProtectionProvider protection, ILookupNormalizer normalizer) : IWorkspaceInvitations
{
    private static string Id(long id) => id.ToString(CultureInfo.InvariantCulture);
    private static void Fail(int status = 409, string code = "invitation.conflict") => throw new InvitationException(status, code);
    private static InvitationDto Map(WorkspaceInvitation i, string? effective = null) => new(Id(i.Id), Id(i.WorkspaceId), i.ProjectId is { } p ? Id(p) : null, i.Email, i.Role, effective ?? i.State, Id(i.Version), i.ExpiresAtUtc, i.Resends, effective is "Expired" or "Revoked" ? "Cancelled" : i.DeliveryState, i.LastSentAtUtc.AddMinutes(1));
    private static async Task Gate(TaskFlowDbContext db, WorkspaceWriteContext c, CancellationToken token)
    {
        if (c.ActorId <= 0) Fail(401, "invitation.unauthenticated");
        if (!(await db.Workspaces.FromSqlInterpolated($"SELECT * FROM `Workspaces` WHERE `Id`={c.WorkspaceId} FOR UPDATE").ToListAsync(token)).Any()) Fail(404, "invitation.unavailable");
    }
    private static async Task Manage(TaskFlowDbContext db, WorkspaceWriteContext c, long? project, CancellationToken token)
    {
        var actor = await db.WorkspaceMemberships.SingleOrDefaultAsync(m => m.WorkspaceId == c.WorkspaceId && m.UserId == c.ActorId && m.State == WorkspaceMembershipState.Active, token);
        if (actor is null) Fail(404, "invitation.unavailable");
        if (project is null) { if (actor!.Role is not (WorkspaceRole.Owner or WorkspaceRole.Admin)) Fail(403, "invitation.forbidden"); }
        else
        {
            var p = await db.Projects.SingleOrDefaultAsync(p => p.WorkspaceId == c.WorkspaceId && p.Id == project && p.DeletedAtUtc == null, token);
            if (p is null || p.OwnerId != c.ActorId) Fail(404, "invitation.unavailable");
            if (actor!.Role is not (WorkspaceRole.Owner or WorkspaceRole.Admin)) Fail(403, "invitation.company_approval_required");
        }
    }
    private async Task<T> Write<T>(WorkspaceWriteContext c, Func<TaskFlowDbContext, DateTime, Task<T>> change, CancellationToken token)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token); await Gate(db, c, token);
        var result = await change(db, await InvitationPolicy.Now(db, token));
        foreach (var transition in db.ChangeTracker.Entries<InvitationTransition>().Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToArray())
        {
            var invitation = await db.WorkspaceInvitations.SingleAsync(i => i.Id == transition.InvitationId && i.WorkspaceId == c.WorkspaceId, token);
            db.WorkspaceAuditLogs.Add(new() { WorkspaceId = c.WorkspaceId, ActorUserId = transition.ActorUserId, TargetUserId = transition.ActorUserId, ProjectId = invitation.ProjectId, Action = "Invitation" + transition.Action, SubjectId = Id(invitation.Id) });
        }
        TaskFlow.Infrastructure.Outbox.OutboxEnqueue.Capture(db);
        await db.SaveChangesAsync(token); await transaction.CommitAsync(token); return result;
    }
    public Task<InvitationPage> ListAsync(WorkspaceWriteContext c, long? project, int page, int size, CancellationToken token) => Write<InvitationPage>(c, async (db, now) =>
    {
        await Manage(db, c, project, token); if (page < 1 || size is < 1 or > 100 || (long)(page - 1) * size > 10000) Fail(400, "invitation.invalid_page");
        var query = db.WorkspaceInvitations.Where(i => i.WorkspaceId == c.WorkspaceId && i.ProjectId == project);
        var count = await query.CountAsync(token); var valid = await InvitationPolicy.Valid(db, c.WorkspaceId, now).Select(i => i.Id).ToArrayAsync(token);
        var rows = await query.OrderByDescending(i => i.Id).Skip((page - 1) * size).Take(size).ToArrayAsync(token);
        return new(rows.Select(i => Map(i, i.State == "Pending" && !valid.Contains(i.Id) ? i.ExpiresAtUtc <= now ? "Expired" : "Revoked" : null)).ToArray(), page, size, count, (int)Math.Ceiling(count / (double)size));
    }, token);
    public Task<InvitationDto> CreateAsync(WorkspaceWriteContext c, string email, long? project, string role, CancellationToken token) => Write(c, async (db, now) =>
    {
        await Manage(db, c, project, token);
        email = email.Trim(); if (!EmailTemplates.ValidRecipient(email) || (project is null ? role is not ("Member" or "Guest") : role is not ("Editor" or "Viewer"))) Fail(400, "invitation.invalid");
        var inviter = await db.Users.SingleAsync(u => u.Id == c.ActorId, token); if (!inviter.EmailConfirmed) Fail(403, "invitation.email_unverified");
        await InvitationPolicy.Sweep(db, c.WorkspaceId, now, token, c.ActorId);
        var normalized = normalizer.NormalizeEmail(email)!;
        if (await db.WorkspaceInvitations.AnyAsync(i => i.WorkspaceId == c.WorkspaceId && i.PendingEmail == normalized, token)) Fail();
        var user = await db.Users.SingleOrDefaultAsync(u => u.NormalizedEmail == normalized, token);
        var member = user is null ? null : await db.WorkspaceMemberships.SingleOrDefaultAsync(m => m.WorkspaceId == c.WorkspaceId && m.UserId == user.Id, token);
        if (member is not null && (member.State != WorkspaceMembershipState.Active || project is null)) Fail();
        if (user is not null && project is not null && (await db.Projects.AnyAsync(p => p.Id == project && p.OwnerId == user.Id, token) || await db.ProjectMembers.AnyAsync(m => m.ProjectId == project && m.UserId == user.Id && m.RevokedAtUtc == null, token))) Fail();
        if (member is null) await PilotQuotas.MemberAsync(db, c.WorkspaceId, token);
        if (await db.WorkspaceInvitations.CountAsync(i => i.WorkspaceId == c.WorkspaceId, token) >= 1000) throw new PilotQuotaException();
        await SendBudget(db, c.WorkspaceId, now, token);
        var invite = new WorkspaceInvitation { WorkspaceId = c.WorkspaceId, InviterUserId = c.ActorId, ProjectId = project, Email = email, NormalizedEmail = normalized, PendingEmail = normalized, Role = role, CreatedAtUtc = now, LastSentAtUtc = now, AvailableAtUtc = now, ExpiresAtUtc = now.AddDays(7) };
        db.WorkspaceInvitations.Add(invite); await db.SaveChangesAsync(token); await Queue(db, invite, now, token);
        InvitationPolicy.Audit(db, invite, c.ActorId, "Created", now); return Map(invite);
    }, token);
    private async Task Queue(TaskFlowDbContext db, WorkspaceInvitation i, DateTime now, CancellationToken token)
    {
        var secret = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        i.TokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))); i.MessageId = Guid.NewGuid().ToString("N"); i.LastSentAtUtc = now; i.DeliveryState = "Pending"; i.Attempts = 0; i.AvailableAtUtc = now; i.LeaseToken = null; i.LeaseExpiresAtUtc = null; i.LastCode = null;
        var company = await db.Workspaces.SingleAsync(w => w.Id == i.WorkspaceId, token);
        var request = new EmailRequest { WorkspaceId = i.WorkspaceId, InvitationId = i.Id, MessageId = i.MessageId, Recipient = i.Email, DisplayName = "Future teammate", CompanyName = company.Name, Template = EmailTemplate.CompanyInvitation, InvitationToken = secret };
        i.ProtectedMessage = protection.CreateProtector("TaskFlow.InvitationMail.v1", Id(i.WorkspaceId), Id(i.Id), i.MessageId).Protect(JsonSerializer.Serialize(request));
    }
    private static async Task<WorkspaceInvitation> Find(TaskFlowDbContext db, WorkspaceWriteContext c, long id, CancellationToken token) => await db.WorkspaceInvitations.SingleOrDefaultAsync(i => i.WorkspaceId == c.WorkspaceId && i.Id == id, token) ?? throw new InvitationException(404, "invitation.unavailable");
    private static async Task SendBudget(TaskFlowDbContext db, long workspace, DateTime now, CancellationToken token)
    {
        // Company row lock serializes admission across actors, projects and processes.
        // Append-only history means revoke/accept does not replenish the send budget.
        var recent = await db.InvitationTransitions
            .Where(t => t.WorkspaceId == workspace && (t.Action == "Created" || t.Action == "Resent") && t.CreatedAtUtc > now.AddHours(-1))
            .OrderByDescending(t => t.CreatedAtUtc).Select(t => t.CreatedAtUtc).Take(20).ToArrayAsync(token);
        if (recent.Length == 20)
        {
            var wait = Math.Clamp((int)Math.Ceiling((recent[^1].AddHours(1) - now).TotalSeconds), 1, 3600);
            throw new InvitationException(429, "invitation.send_limited", wait);
        }
    }
    public Task<InvitationDto> ResendAsync(WorkspaceWriteContext c, long id, long version, CancellationToken token) => Write(c, async (db, now) =>
    {
        var i = await Find(db, c, id, token); await Manage(db, c, i.ProjectId, token);
        if (i.Version != version || i.State != "Pending" || !await InvitationPolicy.Valid(db, c.WorkspaceId, now).AnyAsync(x => x.Id == id, token)) Fail();
        if (i.Resends >= 3 || i.LastSentAtUtc > now.AddMinutes(-1)) Fail(429, "invitation.resend_limited");
        if (!await db.Users.AnyAsync(u => u.Id == c.ActorId && u.EmailConfirmed, token)) Fail(403, "invitation.email_unverified");
        await SendBudget(db, c.WorkspaceId, now, token);
        i.Resends++; i.Version++; await Queue(db, i, now, token); InvitationPolicy.Audit(db, i, c.ActorId, "Resent", now); return Map(i);
    }, token);
    public async Task RevokeAsync(WorkspaceWriteContext c, long id, long version, CancellationToken token) => await Write(c, async (db, now) =>
    {
        var i = await Find(db, c, id, token); await Manage(db, c, i.ProjectId, token); if (i.Version != version || i.State != "Pending") Fail();
        InvitationPolicy.Finish(i, "Revoked"); InvitationPolicy.Audit(db, i, c.ActorId, "Revoked", now); return true;
    }, token);
    public Task<InvitationPreviewDto> PreviewAsync(WorkspaceWriteContext c, long id, string secret, bool accept, CancellationToken token) => Write<InvitationPreviewDto>(c, async (db, now) =>
    {
        var i = await Find(db, c, id, token); var user = await db.Users.SingleOrDefaultAsync(u => u.Id == c.ActorId, token);
        if (user is null || !user.EmailConfirmed || user.NormalizedEmail != i.NormalizedEmail || secret.Length != 43 || i.TokenHash is null || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)), Convert.FromHexString(i.TokenHash))) Fail(404, "invitation.unavailable");
        if (i.State != "Pending" || !await InvitationPolicy.Valid(db, c.WorkspaceId, now).AnyAsync(x => x.Id == id, token)) Fail(409, "invitation.unavailable");
        var project = i.ProjectId is { } p ? await db.Projects.SingleAsync(x => x.WorkspaceId == c.WorkspaceId && x.Id == p, token) : null;
        if (accept)
        {
            var member = await db.WorkspaceMemberships.SingleOrDefaultAsync(m => m.WorkspaceId == c.WorkspaceId && m.UserId == c.ActorId, token);
            if (member is not null && (member.State != WorkspaceMembershipState.Active || project is null)) Fail();
            if (project is not null && (project.OwnerId == c.ActorId || await db.ProjectMembers.AnyAsync(m => m.ProjectId == project.Id && m.UserId == c.ActorId && m.RevokedAtUtc == null, token))) Fail();
            InvitationPolicy.Finish(i, "Accepted"); i.AcceptedUserId = c.ActorId; await db.SaveChangesAsync(token);
            if (member is null)
            {
                await PilotQuotas.MemberAsync(db, c.WorkspaceId, token);
                db.WorkspaceMemberships.Add(new() { WorkspaceId = c.WorkspaceId, UserId = c.ActorId, Role = project is null ? Enum.Parse<WorkspaceRole>(i.Role) : WorkspaceRole.Guest });
                await db.SaveChangesAsync(token);
            }
            if (project is not null)
            {
                var grant = await db.ProjectMembers.SingleOrDefaultAsync(m => m.ProjectId == project.Id && m.UserId == c.ActorId, token);
                if (grant is null) db.ProjectMembers.Add(new() { WorkspaceId = c.WorkspaceId, ProjectId = project.Id, UserId = c.ActorId, Role = Enum.Parse<MembershipRole>(i.Role) });
                else { grant.WorkspaceId = c.WorkspaceId; grant.Role = Enum.Parse<MembershipRole>(i.Role); grant.RevokedAtUtc = null; }
            }
            InvitationPolicy.Audit(db, i, c.ActorId, "Accepted", now);
        }
        var company = await db.Workspaces.SingleAsync(w => w.Id == c.WorkspaceId, token);
        return new(Id(c.WorkspaceId), company.Name, i.ProjectId is { } projectId ? Id(projectId) : null, project?.Name, i.Role, i.ExpiresAtUtc);
    }, token);
}
