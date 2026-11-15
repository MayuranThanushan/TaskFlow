using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TaskFlow.Application.Features.Email;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Infrastructure.Workspaces;
namespace TaskFlow.Infrastructure.Email;
public sealed class InvitationMailCapture(TaskFlowDbContext db, IDataProtectionProvider protection, IEmailSender sender)
{
    public async Task<int> RunAsync(CancellationToken token = default)
    {
        int handled = 0;
        for (var n = 0; n < 25; n++)
        {
            if (db.ChangeTracker.HasChanges()) throw new InvalidOperationException("Invitation capture requires a clean context."); db.ChangeTracker.Clear();
            var candidate = await db.WorkspaceInvitations.FromSqlRaw("SELECT * FROM WorkspaceInvitations WHERE State='Pending' AND ((DeliveryState='Pending' AND AvailableAtUtc<=UTC_TIMESTAMP(6)) OR (DeliveryState='Leased' AND LeaseExpiresAtUtc<=UTC_TIMESTAMP(6)))").AsNoTracking().OrderBy(i => i.AvailableAtUtc).ThenBy(i => i.Id).Select(i => new { i.Id, i.WorkspaceId }).FirstOrDefaultAsync(token);
            if (candidate is null) break;
            TaskFlow.Domain.Entities.WorkspaceInvitation work; string lease;
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
            {
                await using var gate = db.Database.GetDbConnection().CreateCommand(); gate.Transaction = transaction.GetDbTransaction(); gate.CommandText = "SELECT Id FROM Workspaces WHERE Id=@workspace FOR SHARE"; var parameter = gate.CreateParameter(); parameter.ParameterName = "@workspace"; parameter.Value = candidate.WorkspaceId; gate.Parameters.Add(parameter); await gate.ExecuteScalarAsync(token);
                var now = await InvitationPolicy.Now(db, token);
                work = (await db.WorkspaceInvitations.FromSqlInterpolated($"SELECT * FROM `WorkspaceInvitations` WHERE `Id`={candidate.Id} AND `WorkspaceId`={candidate.WorkspaceId} FOR UPDATE").ToListAsync(token)).Single();
                if (work.State == "Pending" && !await InvitationPolicy.Valid(db, candidate.WorkspaceId, now).AnyAsync(i => i.Id == work.Id, token))
                { work.DeliveryState = "Cancelled"; work.ProtectedMessage = null; work.LeaseToken = null; work.LeaseExpiresAtUtc = null; work.LastCode = "invitation_mail.unavailable"; await db.SaveChangesAsync(token); await transaction.CommitAsync(token); handled++; continue; }
                if (work.State != "Pending") { await transaction.CommitAsync(token); handled++; continue; }
                if (!(work.DeliveryState == "Pending" && work.AvailableAtUtc <= now || work.DeliveryState == "Leased" && work.LeaseExpiresAtUtc <= now)) { await transaction.CommitAsync(token); break; }
                if (work.Attempts >= 5) { work.DeliveryState = "DeadLetter"; work.LastCode = "invitation_mail.exhausted"; work.ProtectedMessage = null; work.LeaseToken = null; work.LeaseExpiresAtUtc = null; await db.SaveChangesAsync(token); await transaction.CommitAsync(token); handled++; continue; }
                work.DeliveryState = "Leased"; work.LeaseToken = lease = Guid.NewGuid().ToString("N"); work.LeaseExpiresAtUtc = now.AddSeconds(60); work.Attempts++;
                await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            }
            // Claiming is not send authority. Reacquire the company gate in the same
            // order as invitation writers and retain it through dispatch/finalization.
            // A revocation that commits first therefore prevents even decryption/send.
            db.ChangeTracker.Clear();
            using var dispatchDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            dispatchDeadline.CancelAfter(TimeSpan.FromSeconds(25));
            var dispatchToken = dispatchDeadline.Token;
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, dispatchToken))
            {
                await using var gate = db.Database.GetDbConnection().CreateCommand(); gate.Transaction = transaction.GetDbTransaction(); gate.CommandText = "SELECT Id FROM Workspaces WHERE Id=@workspace FOR SHARE";
                var parameter = gate.CreateParameter(); parameter.ParameterName = "@workspace"; parameter.Value = work.WorkspaceId; gate.Parameters.Add(parameter); await gate.ExecuteScalarAsync(dispatchToken);
                var current = (await db.WorkspaceInvitations.FromSqlInterpolated($"SELECT * FROM `WorkspaceInvitations` WHERE `Id`={work.Id} AND `WorkspaceId`={work.WorkspaceId} FOR UPDATE").ToListAsync(dispatchToken)).Single();
                var now = await InvitationPolicy.Now(db, dispatchToken);
                if (current.State != "Pending" || current.DeliveryState != "Leased" || current.MessageId != work.MessageId || current.LeaseToken != lease || current.LeaseExpiresAtUtc is null || current.LeaseExpiresAtUtc <= now)
                { await transaction.CommitAsync(dispatchToken); continue; }
                if (!await InvitationPolicy.Valid(db, current.WorkspaceId, now).AnyAsync(i => i.Id == current.Id, dispatchToken))
                {
                    current.DeliveryState = "Cancelled"; current.ProtectedMessage = null; current.LeaseToken = null; current.LeaseExpiresAtUtc = null; current.LastCode = "invitation_mail.unavailable";
                    await db.SaveChangesAsync(dispatchToken); await transaction.CommitAsync(dispatchToken); handled++; continue;
                }
                EmailResult result;
                try
                {
                    var plain = protection.CreateProtector("TaskFlow.InvitationMail.v1", current.WorkspaceId.ToString(), current.Id.ToString(), current.MessageId).Unprotect(current.ProtectedMessage!);
                    var request = JsonSerializer.Deserialize<EmailRequest>(plain);
                    if (request is null || request.WorkspaceId != current.WorkspaceId || request.InvitationId != current.Id || request.AccountUserId is not null || request.MessageId != current.MessageId || request.Template != EmailTemplate.CompanyInvitation) throw new CryptographicException();
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(dispatchToken); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                    result = await sender.SendAsync(request, deadline.Token).WaitAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (dispatchToken.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { result = new(EmailStatus.InvalidRequest, "invitation_mail.invalid_payload"); }
                catch (Exception) { result = new(EmailStatus.CaptureUnavailable, "email.capture_unavailable"); }
                now = await InvitationPolicy.Now(db, dispatchToken);
                // A lease can expire during a stalled dispatch; never acknowledge stale ownership.
                if (current.LeaseExpiresAtUtc <= now) { await transaction.CommitAsync(dispatchToken); continue; }
                current.LastCode = result.Code is "email.captured" or "email.already_captured" or "email.company_limit" or "email.capture_unavailable" or "email.recipient_blocked" or "email.disabled" or "email.configuration_invalid" or "email.id_conflict" or "invitation_mail.invalid_payload" or "email.provider_accepted" or "email.provider_rate_limited" or "email.provider_unavailable" or "email.provider_auth_failed" or "email.provider_rejected" ? result.Code : "email.capture_unavailable";
                if (current.ExpiresAtUtc <= now) { current.DeliveryState = "Cancelled"; current.LastCode = "invitation_mail.expired"; }
                else if (result.IsCaptured) current.DeliveryState = "Captured";
                else if (result.Status == EmailStatus.ProviderAccepted) current.DeliveryState = "ProviderAccepted";
                else if (result.Status is EmailStatus.CaptureUnavailable or EmailStatus.ProviderUnavailable or EmailStatus.RateLimited && current.Attempts < 5) { current.DeliveryState = "Pending"; current.AvailableAtUtc = result.RetryAtUtc?.UtcDateTime ?? now.AddSeconds(5 * (1 << (current.Attempts - 1))); }
                else current.DeliveryState = "DeadLetter";
                if (current.DeliveryState != "Pending") current.ProtectedMessage = null; current.LeaseToken = null; current.LeaseExpiresAtUtc = null;
                InvitationPolicy.Audit(db, current, current.InviterUserId, current.DeliveryState is "Captured" or "ProviderAccepted" ? current.DeliveryState : "DeliveryAttempted", now);
                await db.SaveChangesAsync(dispatchToken); await transaction.CommitAsync(dispatchToken); handled++;
            }
        }
        return handled;
    }
}
