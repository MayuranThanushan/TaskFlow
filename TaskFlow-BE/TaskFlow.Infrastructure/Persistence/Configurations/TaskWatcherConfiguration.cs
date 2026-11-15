using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
namespace TaskFlow.Infrastructure.Persistence.Configurations;
public sealed class TaskWatcherConfiguration : IEntityTypeConfiguration<TaskWatcher>
{
    public void Configure(EntityTypeBuilder<TaskWatcher> b)
    {
        b.HasKey(w => new { w.WorkspaceId, w.TaskId, w.UserId });
        b.Property(w => w.Version).HasDefaultValue(1L).IsConcurrencyToken();
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(w => new { w.WorkspaceId, w.UserId });
    }
}
public sealed class WatcherNotificationIntentConfiguration : IEntityTypeConfiguration<WatcherNotificationIntent>
{
    public void Configure(EntityTypeBuilder<WatcherNotificationIntent> b)
    {
        b.HasKey(w => w.Id); b.Property(w => w.State).HasMaxLength(16); b.Property(w => w.Outcome).HasMaxLength(24);
        b.HasIndex(w => new { w.WorkspaceId, w.RecipientUserId, w.EventId }).IsUnique();
        b.HasIndex(w => new { w.WorkspaceId, w.State, w.AvailableAtUtc, w.Id });
        b.HasOne<TaskWatcher>().WithMany().HasForeignKey(w => new { w.WorkspaceId, w.TaskId, UserId = w.RecipientUserId });
        b.HasOne(w => w.Event).WithMany().HasForeignKey(w => new { w.WorkspaceId, w.EventId }).HasPrincipalKey(e => new { e.WorkspaceId, e.Id });
        b.HasOne<TaskComment>().WithMany().HasForeignKey(w => new { w.WorkspaceId, Id = w.CommentId }).HasPrincipalKey(c => new { c.WorkspaceId, c.Id });
        b.ToTable(t => t.HasCheckConstraint("CK_WatcherNotificationIntents_State", "(State = 'Pending' AND Outcome IS NULL AND CompletedAtUtc IS NULL) OR (State = 'Delivered' AND Outcome = 'InAppDelivered' AND CompletedAtUtc IS NOT NULL) OR (State = 'Suppressed' AND Outcome IN ('Expired','Unwatched','Revoked','Unavailable','Muted') AND CompletedAtUtc IS NOT NULL)"));
    }
}
