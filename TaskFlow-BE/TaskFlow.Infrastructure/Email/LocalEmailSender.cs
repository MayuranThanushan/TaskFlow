using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Features.Email;

namespace TaskFlow.Infrastructure.Email;

public sealed class LocalEmailOptions
{
    public string Mode { get; set; } = "Disabled";
    public string CaptureDirectory { get; set; } = "";
    public string PublicOrigin { get; set; } = "";
    public int PerCompanyHourlyLimit { get; set; } = 20;
    public string[] AllowedRecipients { get; set; } = [];
}

// A local/staging file sink only: no SMTP, HTTP provider or production network path.
public sealed class LocalEmailSender(LocalEmailOptions options, string environment, TimeProvider clock) : IEmailSender
{
    private sealed record Receipt(int Version, string MessageId, long WorkspaceId, EmailTemplate Template, DateTimeOffset CapturedAtUtc, string Fingerprint, string MimeSha256, string Status);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<EmailResult> SendAsync(EmailRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (options.Mode != "LocalCapture" || environment is not ("Development" or "Staging")) return new(EmailStatus.Disabled, "email.disabled");
        if (request is null || !EmailTemplates.ValidRequest(request)) return new(EmailStatus.InvalidRequest, "email.invalid");
        if (!ValidConfiguration(out var origin)) return new(EmailStatus.Disabled, "email.configuration_invalid");
        var synthetic = request.Recipient.EndsWith(".test", StringComparison.OrdinalIgnoreCase);
        var allowed = (options.AllowedRecipients ?? []).Contains(request.Recipient, StringComparer.OrdinalIgnoreCase);
        if (!(environment == "Development" && synthetic) && !allowed) return new(EmailStatus.RecipientBlocked, "email.recipient_blocked");
        var id = Guid.ParseExact(request.MessageId, "N").ToString("N");
        var now = clock.GetUtcNow();
        // Include origin/template/body/recipient in identity so changed requests cannot reuse a receipt.
        var body = EmailTemplates.Render(request, origin!);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request.WorkspaceId, Recipient = request.Recipient, Template = request.Template, body.Subject, body.Text, body.Html }, Json))));
        string? temporary = null;
        try
        {
            var company = Path.Combine(Path.GetFullPath(options.CaptureDirectory), request.WorkspaceId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            EnsurePrivateDirectory(company);
            // FileShare.None is a cross-process lock. No unbounded lock wait; caller may retry.
            using var gate = new FileStream(Path.Combine(company, ".capture.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var destination = Path.Combine(company, id);
            if (Directory.Exists(destination))
            {
                EnsurePrivateDirectory(destination);
                var receipt = await ReadReceipt(destination, token);
                if (receipt is null || receipt.Version != 1 || receipt.WorkspaceId != request.WorkspaceId || receipt.MessageId != id || receipt.Status != "Captured") return new(EmailStatus.CaptureUnavailable, "email.capture_unavailable");
                var messageFile = Path.Combine(destination, "message.eml");
                if (!File.Exists(messageFile) || new FileInfo(messageFile).Length > 65_536 || (File.GetAttributes(messageFile) & FileAttributes.ReparsePoint) != 0 || Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(messageFile, token))) != receipt.MimeSha256) return new(EmailStatus.CaptureUnavailable, "email.capture_unavailable");
                return receipt.Fingerprint == fingerprint ? new(EmailStatus.AlreadyCaptured, "email.already_captured") : new(EmailStatus.IdConflict, "email.id_conflict");
            }
            int currentHour = 0, total = 0;
            var hour = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
            foreach (var directory in Directory.EnumerateDirectories(company))
            {
                if (Path.GetFileName(directory).StartsWith('.')) continue; // interrupted pre-commit captures are not receipts
                EnsurePrivateDirectory(directory);
                if (++total >= 10_000) return new(EmailStatus.RateLimited, "email.capture_storage_limit");
                var receipt = await ReadReceipt(directory, token);
                if (receipt is null || receipt.WorkspaceId != request.WorkspaceId || receipt.Version != 1 || receipt.Status != "Captured") return new(EmailStatus.CaptureUnavailable, "email.capture_unavailable");
                if (receipt.CapturedAtUtc >= hour) currentHour++;
            }
            if (currentHour >= options.PerCompanyHourlyLimit) return new(EmailStatus.RateLimited, "email.company_limit", hour.AddHours(1));
            temporary = Path.Combine(company, ".pending-" + Guid.NewGuid().ToString("N"));
            EnsurePrivateDirectory(temporary);
            var mime = EmailTemplates.Mime(request, body, now);
            await File.WriteAllBytesAsync(Path.Combine(temporary, "message.eml"), mime, token);
            await File.WriteAllTextAsync(Path.Combine(temporary, "receipt.json"), JsonSerializer.Serialize(new Receipt(1, id, request.WorkspaceId, request.Template, now, fingerprint, Convert.ToHexString(SHA256.HashData(mime)), "Captured"), Json), token);
            RestrictFile(Path.Combine(temporary, "message.eml")); RestrictFile(Path.Combine(temporary, "receipt.json"));
            token.ThrowIfCancellationRequested();
            // Both message and receipt become visible together on the same local filesystem.
            Directory.Move(temporary, destination); temporary = null;
            return new(EmailStatus.Captured, "email.captured");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException)
        { return new(EmailStatus.CaptureUnavailable, "email.capture_unavailable"); }
        finally
        {
            if (temporary is not null)
                try { Directory.Delete(temporary, true); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        }
    }
    private static async Task<Receipt?> ReadReceipt(string directory, CancellationToken token)
    {
        var path = Path.Combine(directory, "receipt.json");
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException();
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 2048) throw new IOException();
        return await JsonSerializer.DeserializeAsync<Receipt>(file, Json, token);
    }
    private bool ValidConfiguration(out Uri? origin)
    {
        origin = null;
        if (options.PerCompanyHourlyLimit is < 1 or > 1000 || !Path.IsPathFullyQualified(options.CaptureDirectory)) return false;
        if (Path.GetFullPath(options.CaptureDirectory) == Path.GetPathRoot(Path.GetFullPath(options.CaptureDirectory))) return false;
        if (options.CaptureDirectory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p.Equals("wwwroot", StringComparison.OrdinalIgnoreCase))) return false;
        if (!Uri.TryCreate(options.PublicOrigin, UriKind.Absolute, out origin) || origin.UserInfo.Length != 0 || origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0) return false;
        return origin.Scheme == "https" || environment == "Development" && origin.Scheme == "http" && origin.IsLoopback;
    }
    private static void EnsurePrivateDirectory(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path); // inherits deployment-owned private ACL
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Refuse reparse points/symlinks through the configured capture tree.
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException();
    }
    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
