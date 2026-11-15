using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
namespace TaskFlow.Infrastructure.Persistence.Configurations;
public sealed class TaskCommentConfiguration : IEntityTypeConfiguration<TaskComment>
{
    public void Configure(EntityTypeBuilder<TaskComment> b)
    {
        b.HasKey(c => c.Id); b.HasAlternateKey(c => new { c.WorkspaceId, c.Id });
        b.Property(c => c.Body).HasMaxLength(5000).IsRequired();
        b.Property(c => c.Version).HasDefaultValue(1L).IsConcurrencyToken();
        b.HasIndex(c => new { c.WorkspaceId, c.TaskId, c.Id });
        b.HasOne<WorkspaceMembership>().WithMany().HasForeignKey(c => new { c.WorkspaceId, UserId = c.AuthorId });
        b.ToTable(t => t.HasCheckConstraint("CK_TaskComments_Body", "(DeletedAtUtc IS NULL AND CHAR_LENGTH(TRIM(Body)) BETWEEN 1 AND 5000) OR (DeletedAtUtc IS NOT NULL AND Body = '')"));
    }
}
public sealed class CommentRevisionConfiguration : IEntityTypeConfiguration<CommentRevision>
{
    public void Configure(EntityTypeBuilder<CommentRevision> b)
    {
        b.HasKey(c => c.Id); b.Property(c => c.Action).HasMaxLength(16);
        b.HasIndex(c => new { c.WorkspaceId, c.CommentId, c.Version }).IsUnique();
        b.HasOne<TaskComment>().WithMany().HasForeignKey(c => new { c.WorkspaceId, Id = c.CommentId }).HasPrincipalKey(c => new { c.WorkspaceId, c.Id });
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(c => c.ActorId);
        b.ToTable(t => t.HasCheckConstraint("CK_CommentRevisions_Action", "Action IN ('Created','Edited','Deleted')"));
    }
}
