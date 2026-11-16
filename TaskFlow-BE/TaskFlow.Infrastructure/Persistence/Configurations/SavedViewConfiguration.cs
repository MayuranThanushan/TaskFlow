using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
namespace TaskFlow.Infrastructure.Persistence.Configurations;
public sealed class SavedViewConfiguration : IEntityTypeConfiguration<SavedView>
{
    public void Configure(EntityTypeBuilder<SavedView> b)
    {
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(100);
        b.Property(x => x.Surface).HasMaxLength(12);
        b.Property(x => x.FiltersJson).HasMaxLength(4000);
        b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);
        b.HasIndex(x => new { x.WorkspaceId, x.UserId, x.Id });
        b.HasIndex(x => new { x.WorkspaceId, x.Shared, x.Id });
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.UserId });
        b.HasOne<Project>().WithMany().HasForeignKey(x => x.ProjectId);
    }
}
public sealed class ViewPreferenceConfiguration : IEntityTypeConfiguration<ViewPreference>
{
    public void Configure(EntityTypeBuilder<ViewPreference> b)
    {
        b.HasKey(x => new { x.WorkspaceId, x.UserId, x.Context });
        b.Property(x => x.Context).HasMaxLength(40);
        b.Property(x => x.Version).IsConcurrencyToken().HasDefaultValue(1L);
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(x => new { x.WorkspaceId, x.UserId });
        // Deliberately no FK to the view: a deleted/inaccessible default becomes unavailable,
        // rather than blocking deletion or granting access through a preference.
    }
}
