using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
namespace TaskFlow.Infrastructure.Persistence.Configurations;
public sealed class InvitationConfiguration : IEntityTypeConfiguration<WorkspaceInvitation>
{
    public void Configure(EntityTypeBuilder<WorkspaceInvitation> b)
    {
        b.HasKey(x => x.Id); b.HasAlternateKey(x => new { x.WorkspaceId, x.Id });
        b.Property(x => x.Email).HasMaxLength(254); b.Property(x => x.NormalizedEmail).HasMaxLength(254); b.Property(x => x.PendingEmail).HasMaxLength(254);
        b.HasIndex(x => new { x.WorkspaceId, x.PendingEmail }).IsUnique(); b.HasIndex(x => new { x.WorkspaceId, x.CreatedAtUtc });
        b.HasIndex(x => new { x.WorkspaceId, x.ProjectId });
        b.HasIndex(x => new { x.DeliveryState, x.AvailableAtUtc, x.Id });
        b.Property(x => x.State).HasMaxLength(16); b.Property(x => x.Role).HasMaxLength(16); b.Property(x => x.TokenHash).HasMaxLength(64);
        b.Property(x => x.MessageId).HasMaxLength(32); b.HasIndex(x => x.MessageId).IsUnique(); b.Property(x => x.ProtectedMessage).HasColumnType("text");
        b.Property(x => x.DeliveryState).HasMaxLength(16); b.Property(x => x.LeaseToken).HasMaxLength(32); b.Property(x => x.LastCode).HasMaxLength(64);
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(x => new { x.WorkspaceId, UserId = x.InviterUserId }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.AcceptedUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class InvitationTransitionConfiguration : IEntityTypeConfiguration<InvitationTransition>
{
    public void Configure(EntityTypeBuilder<InvitationTransition> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.Action).HasMaxLength(32); b.Property(x => x.CorrelationId).HasMaxLength(64);
        b.HasOne<WorkspaceInvitation>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.InvitationId }).HasPrincipalKey(x => new { x.WorkspaceId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.WorkspaceId, x.InvitationId, x.Id });
    }
}
