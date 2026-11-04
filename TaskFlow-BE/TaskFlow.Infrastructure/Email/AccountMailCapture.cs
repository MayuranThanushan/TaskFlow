using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using TaskFlow.Application.Features.Email;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Persistence;
namespace TaskFlow.Infrastructure.Email;
// Only explicit recipient-bound account jobs; never tenant checkpoint source events.
public sealed class AccountMailCapture(TaskFlowDbContext db, IDataProtectionProvider protection, IEmailSender sender)
{
    private static async Task<DateTime> Now(TaskFlowDbContext db, CancellationToken token)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand(); command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction(); command.CommandText = "SELECT UTC_TIMESTAMP(6)";
        return DateTime.SpecifyKind(Convert.ToDateTime(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
    }
    public async Task<int> RunAsync(int maximum = 25, CancellationToken token = default)
    {
        if (maximum is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(maximum));
        int handled = 0;
        for (var i = 0; i < maximum; i++)
        {
            db.ChangeTracker.Clear(); AccountEmailJob job; string lease;
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
            {
                var rows = await db.AccountEmailJobs.FromSqlRaw("SELECT * FROM `AccountEmailJobs` WHERE (`State`='Pending' AND `AvailableAtUtc`<=UTC_TIMESTAMP(6)) OR (`State`='Leased' AND `LeaseExpiresAtUtc`<=UTC_TIMESTAMP(6)) ORDER BY `AvailableAtUtc`,`Id` LIMIT 1 FOR UPDATE SKIP LOCKED").ToListAsync(token);
                if (rows.Count == 0) { await transaction.CommitAsync(token); break; }
                job = rows[0]; var now = await Now(db, token);
                if (job.ExpiresAtUtc <= now || job.Attempts >= 5)
                {
                    job.State = job.ExpiresAtUtc <= now ? "Cancelled" : "DeadLetter"; job.LastCode = job.ExpiresAtUtc <= now ? "account_mail.expired" : "account_mail.exhausted";
                    job.ProtectedPayload = null; job.LeaseToken = null; job.LeaseExpiresAtUtc = null;
                    await db.SaveChangesAsync(token); await transaction.CommitAsync(token); handled++; continue;
                }
                job.State = "Leased"; job.Attempts++; job.LeaseToken = lease = Guid.NewGuid().ToString("N"); job.LeaseExpiresAtUtc = now.AddSeconds(60);
                await db.SaveChangesAsync(token); await transaction.CommitAsync(token);
            }
            EmailResult result;
            try
            {
                var value = protection.CreateProtector("TaskFlow.AccountMail.v1", job.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), job.MessageId).Unprotect(job.ProtectedPayload!);
                var request = JsonSerializer.Deserialize<EmailRequest>(value);
                if (request is null || request.AccountUserId != job.UserId || request.WorkspaceId != 0 || request.MessageId != job.MessageId || request.Template.ToString() != job.Kind || request.Template is not (EmailTemplate.EmailVerification or EmailTemplate.PasswordRecovery)) throw new CryptographicException();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
                result = await sender.SendAsync(request, deadline.Token).WaitAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is CryptographicException or JsonException or ArgumentException) { result = new(EmailStatus.InvalidRequest, "account_mail.invalid_payload"); }
            catch (Exception) { result = new(EmailStatus.CaptureUnavailable, "email.capture_unavailable"); }
            db.ChangeTracker.Clear();
            await using (var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, token))
            {
                var rows = await db.AccountEmailJobs.FromSqlInterpolated($"SELECT * FROM `AccountEmailJobs` WHERE `Id`={job.Id} FOR UPDATE").ToListAsync(token);
                var current = rows.Single(); var now = await Now(db, token);
                if (current.State != "Leased" || current.LeaseToken != lease || current.LeaseExpiresAtUtc <= now) { await transaction.CommitAsync(token); continue; }
                current.LastCode = SafeCode(result.Code);
                if (current.ExpiresAtUtc <= now) { current.State = "Cancelled"; current.LastCode = "account_mail.expired"; }
                else if (result.IsCaptured) current.State = "Captured";
                else if (result.Status is EmailStatus.CaptureUnavailable or EmailStatus.RateLimited && current.Attempts < 5)
                { current.State = "Pending"; current.AvailableAtUtc = result.RetryAtUtc?.UtcDateTime ?? now.AddSeconds(5 * (1 << (current.Attempts - 1))); }
                else current.State = "DeadLetter";
                if (current.State != "Pending") current.ProtectedPayload = null;
                current.LeaseToken = null; current.LeaseExpiresAtUtc = null;
                await db.SaveChangesAsync(token); await transaction.CommitAsync(token); handled++;
            }
        }
        return handled;
    }
    private static string SafeCode(string code) => code is "email.captured" or "email.already_captured" or "email.disabled" or "email.configuration_invalid" or "email.recipient_blocked" or "email.account_limit" or "email.capture_storage_limit" or "email.capture_unavailable" or "email.id_conflict" or "email.invalid" or "account_mail.invalid_payload" ? code : "email.capture_unavailable";
}
