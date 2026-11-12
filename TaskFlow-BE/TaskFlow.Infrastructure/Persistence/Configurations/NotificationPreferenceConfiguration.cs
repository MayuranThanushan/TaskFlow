using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Persistence.Configurations;

public sealed class NotificationPreferenceConfiguration : IEntityTypeConfiguration<NotificationPreference>
{
    public void Configure(EntityTypeBuilder<NotificationPreference> b)
    {
        b.HasKey(p => new { p.WorkspaceId, p.UserId });
        b.Property(p => p.InAppEnabled).HasDefaultValue(true).HasSentinel(true);
        b.Property(p => p.TaskAssignedEnabled).HasDefaultValue(true).HasSentinel(true);
        b.Property(p => p.Version).HasDefaultValue(1L).IsConcurrencyToken();
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(p => new { p.WorkspaceId, p.UserId });
    }
}
