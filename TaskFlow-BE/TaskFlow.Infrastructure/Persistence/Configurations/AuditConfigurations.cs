using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Persistence.Configurations;

public sealed class ActivityAuditConfiguration : IEntityTypeConfiguration<ActivityLog>
{
    public void Configure(EntityTypeBuilder<ActivityLog> builder) => AuditColumns.Configure(builder);
}
public sealed class GovernanceAuditConfiguration : IEntityTypeConfiguration<WorkspaceAuditLog>
{
    public void Configure(EntityTypeBuilder<WorkspaceAuditLog> builder) => AuditColumns.Configure(builder);
}
internal static class AuditColumns
{
    public static void Configure<T>(EntityTypeBuilder<T> builder) where T : class, IVersionedAudit
    {
        builder.Property(a => a.SchemaVersion).HasDefaultValue(0);
        builder.Property(a => a.Outcome).HasMaxLength(30);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.Property(a => a.SubjectType).HasMaxLength(40);
        builder.Property(a => a.SubjectId).HasMaxLength(80);
        builder.Property(a => a.ChangesJson).HasColumnType("text");
    }
}
public sealed class SecurityEventConfiguration : IEntityTypeConfiguration<SecurityEvent>
{
    public void Configure(EntityTypeBuilder<SecurityEvent> builder)
    {
        builder.ToTable("SecurityEvents"); builder.HasKey(a => a.Id);
        builder.Property(a => a.Action).HasMaxLength(40).IsRequired();
        builder.Property(a => a.Outcome).HasMaxLength(30).IsRequired();
        builder.Property(a => a.SubjectType).HasMaxLength(40).IsRequired();
        builder.Property(a => a.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.RouteTemplate).HasMaxLength(200);
        builder.HasIndex(a => new { a.CreatedAtUtc, a.Id });
        builder.HasIndex(a => new { a.SubjectUserId, a.CreatedAtUtc, a.Id });
        // Platform history retains stable subject IDs without company membership or deletion cascades.
        // No navigation or company API is allowed to traverse this table.
    }
}
