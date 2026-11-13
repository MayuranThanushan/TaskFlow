using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;

namespace TaskFlow.Infrastructure.Persistence.Configurations;

public sealed class UserNotificationConfiguration : IEntityTypeConfiguration<UserNotification>
{
    public void Configure(EntityTypeBuilder<UserNotification> b)
    {
        b.HasKey(n => n.Id);
        b.Property(n => n.ScheduleKey).HasMaxLength(100).UseCollation("ascii_bin");
        b.HasIndex(n => new { n.WorkspaceId, n.RecipientUserId, n.ScheduleKey }).IsUnique();
        b.Property(n => n.Kind).HasMaxLength(32).IsRequired();
        b.HasIndex(n => new { n.WorkspaceId, n.RecipientUserId, n.EventId }).IsUnique();
        b.HasIndex(n => new { n.WorkspaceId, n.RecipientUserId, n.Id });
        b.HasIndex(n => new { n.WorkspaceId, n.RecipientUserId, n.ReadAtUtc, n.Id });
        b.HasOne(n => n.Event).WithMany().HasForeignKey(n => new { n.WorkspaceId, n.EventId }).HasPrincipalKey(e => new { e.WorkspaceId, e.Id });
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(n => new { n.WorkspaceId, UserId = n.RecipientUserId });
        b.ToTable(t => t.HasCheckConstraint("CK_UserNotifications_Kind", "(Kind = 'TaskAssigned' AND EventId IS NOT NULL AND ScheduleKey IS NULL AND SourceDueDateUtc IS NULL) OR (Kind IN ('TaskDue', 'TaskDigest') AND EventId IS NULL AND ScheduleKey IS NOT NULL AND SourceDueDateUtc IS NOT NULL)"));
    }
}
