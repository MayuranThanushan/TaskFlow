using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
namespace TaskFlow.Infrastructure.Persistence.Configurations;
public sealed class NotificationScheduleCursorConfiguration : IEntityTypeConfiguration<NotificationScheduleCursor>
{
    public void Configure(EntityTypeBuilder<NotificationScheduleCursor> b)
    {
        b.HasKey(c => c.WorkspaceId);
        b.HasOne<Workspace>().WithMany().HasForeignKey(c => c.WorkspaceId);
    }
}
