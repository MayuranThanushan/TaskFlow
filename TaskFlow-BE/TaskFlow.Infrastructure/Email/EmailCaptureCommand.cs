using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Features.Email;

namespace TaskFlow.Infrastructure.Email;

public static class EmailCaptureCommand
{
    public static async Task<int> RunAsync(string requestFile, IEmailSender sender, TextWriter output, CancellationToken token = default)
    {
        EmailResult result;
        try
        {
            // A private bounded file avoids addresses/tokens in process arguments or logs.
            await using var input = new FileStream(requestFile, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 16_384) result = new(EmailStatus.InvalidRequest, "email.invalid");
            else
            {
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 4 };
                json.Converters.Add(new JsonStringEnumConverter<EmailTemplate>(allowIntegerValues: false));
                var request = await JsonSerializer.DeserializeAsync<EmailRequest>(input, json, token);
                result = request is null ? new(EmailStatus.InvalidRequest, "email.invalid") : await sender.SendAsync(request, token);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or System.Security.SecurityException)
        { result = new(EmailStatus.InvalidRequest, "email.request_unreadable"); }
        await output.WriteLineAsync(JsonSerializer.Serialize(new { eventId = 3400, code = result.Code, status = result.Status.ToString(), retryAtUtc = result.RetryAtUtc }));
        return result.IsAccepted ? 0 : result.Status is EmailStatus.CaptureUnavailable or EmailStatus.ProviderUnavailable or EmailStatus.RateLimited ? 1 : 2;
    }
}
