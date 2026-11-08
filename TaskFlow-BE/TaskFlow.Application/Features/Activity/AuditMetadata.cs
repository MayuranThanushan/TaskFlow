using System.Text.Json;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Application.Features.Activity;

public sealed record AuditChangeDto(string Field, string? Before, string? After, bool Redacted);
public sealed record AuditMetadataDto(int SchemaVersion, bool IsLegacy, string Outcome, string? CorrelationId, string? SubjectType, string? SubjectId, IReadOnlyList<AuditChangeDto> Changes);
public static class AuditMetadata
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static AuditMetadataDto Map(IVersionedAudit row)
    {
        if (row.SchemaVersion != 1) return new(row.SchemaVersion, row.SchemaVersion == 0,
            row.SchemaVersion == 0 ? "LegacyUnknown" : "UnsupportedVersion", null, null, null, []);
        return new(1, false, row.Outcome ?? "Unknown", row.CorrelationId, row.SubjectType, row.SubjectId,
            row.ChangesJson is null ? [] : JsonSerializer.Deserialize<AuditChangeDto[]>(row.ChangesJson, Json) ?? []);
    }
}
