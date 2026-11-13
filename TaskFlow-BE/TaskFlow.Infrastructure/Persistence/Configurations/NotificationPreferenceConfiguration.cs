using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Persistence.Configurations;

public sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.Property(p => p.TimeZoneId).HasMaxLength(100).HasDefaultValue("UTC");
        b.Property(p => p.DigestHour).HasDefaultValue(9).HasSentinel(9);
        b.ToTable(t => t.HasCheckConstraint("CK_NotificationPreferences_Hour", "DigestHour BETWEEN 0 AND 23"));
        b.HasKey(p => new { p.WorkspaceId, p.UserId });
        b.Property(p => p.InAppEnabled).HasDefaultValue(true).HasSentinel(true);
        b.Property(p => p.TaskAssignedEnabled).HasDefaultValue(true).HasSentinel(true);
        b.Property(p => p.Version).HasDefaultValue(1L).IsConcurrencyToken();
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(p => new { p.WorkspaceId, p.UserId });
    }
}
