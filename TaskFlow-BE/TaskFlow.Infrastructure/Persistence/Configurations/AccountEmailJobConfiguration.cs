using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
namespace TaskFlow.Infrastructure.Persistence.Configurations;
public sealed class AccountEmailJobConfiguration : IEntityTypeConfiguration<AccountEmailJob>
{
    public void Configure(EntityTypeBuilder<AccountEmailJob> b)
    {
        b.HasKey(x => x.Id); b.Property(x => x.MessageId).HasMaxLength(32); b.HasIndex(x => x.MessageId).IsUnique();
        b.Property(x => x.Kind).HasMaxLength(32); b.Property(x => x.State).HasMaxLength(16);
        b.Property(x => x.ProtectedPayload).HasColumnType("text"); b.Property(x => x.LeaseToken).HasMaxLength(32); b.Property(x => x.LastCode).HasMaxLength(64);
        b.HasIndex(x => new { x.State, x.AvailableAtUtc, x.Id }); b.HasIndex(x => new { x.UserId, x.CreatedAtUtc });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
