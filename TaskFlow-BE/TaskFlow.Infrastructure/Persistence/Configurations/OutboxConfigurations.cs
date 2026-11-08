using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
namespace TaskFlow.Infrastructure.Persistence.Configurations;

public sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> b)
    {
        b.HasKey(e => e.Id); b.HasAlternateKey(e => new { e.WorkspaceId, e.Id });
        b.HasIndex(e => e.MessageId).IsUnique(); b.Property(e => e.MessageId).HasMaxLength(32);
        b.Property(e => e.Kind).HasMaxLength(80).IsRequired(); b.Property(e => e.CorrelationId).HasMaxLength(55).IsRequired();
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(e => new { e.WorkspaceId, e.ActorUserId });
        b.ToTable(t => t.HasCheckConstraint("CK_OutboxEvents_TaskParent", "TaskId IS NULL OR ProjectId IS NOT NULL"));
    }
}
public sealed class OutboxDeliveryConfiguration : IEntityTypeConfiguration<OutboxDelivery>
{
    public void Configure(EntityTypeBuilder<OutboxDelivery> b)
    {
        b.HasKey(d => d.Id); b.HasAlternateKey(d => new { d.WorkspaceId, d.Id });
        b.HasIndex(d => new { d.EventId, d.Consumer }).IsUnique(); b.Property(d => d.Consumer).HasMaxLength(40);
        b.HasOne(d => d.Event).WithMany().HasForeignKey(d => new { d.WorkspaceId, d.EventId }).HasPrincipalKey(e => new { e.WorkspaceId, e.Id });
        b.Property(d => d.AvailableAtUtc).HasDefaultValueSql("CURRENT_TIMESTAMP(6)");
        b.Property(d => d.State).HasConversion<string>().HasMaxLength(20);
        b.Property(d => d.LeaseToken).HasMaxLength(32); b.Property(d => d.LastFailureCode).HasMaxLength(32);
        b.HasIndex(d => new { d.State, d.AvailableAtUtc, d.Id }); b.HasIndex(d => new { d.State, d.LeaseExpiresAtUtc, d.Id });
    }
}
public sealed class OutboxAttemptConfiguration : IEntityTypeConfiguration<OutboxAttempt>
{
    public void Configure(EntityTypeBuilder<OutboxAttempt> b)
    {
        b.HasKey(a => a.Id); b.Property(a => a.FailureCode).HasMaxLength(32); b.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(20);
        b.HasOne<OutboxDelivery>().WithMany().HasForeignKey(a => new { a.WorkspaceId, a.DeliveryId }).HasPrincipalKey(d => new { d.WorkspaceId, d.Id });
        b.HasIndex(a => new { a.DeliveryId, a.Attempt, a.Outcome }).IsUnique();
    }
}
