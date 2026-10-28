using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;

namespace TaskFlow.Infrastructure.Persistence.Configurations;

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> builder)
    {
        builder.ToTable("Workspaces", table => table.HasCheckConstraint("CK_Workspaces_Name", "CHAR_LENGTH(TRIM(`Name`)) > 0"));
        builder.HasKey(workspace => workspace.Id);
        builder.Property(workspace => workspace.Name).HasMaxLength(150).IsRequired();
        builder.HasIndex(workspace => workspace.LegacyOwnerId).IsUnique();
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(workspace => workspace.LegacyOwnerId);
    }
}

public sealed class WorkspaceMembershipConfiguration : IEntityTypeConfiguration<WorkspaceMembership>
{
    public void Configure(EntityTypeBuilder<WorkspaceMembership> builder)
    {
        builder.ToTable("WorkspaceMemberships", table =>
        {
            table.HasCheckConstraint("CK_WorkspaceMemberships_Role", "`Role` IN ('Owner','Admin','Member','Guest')");
            table.HasCheckConstraint("CK_WorkspaceMemberships_State", "`State` IN ('Active','Suspended','Removed')");
        });
        builder.HasKey(membership => new { membership.WorkspaceId, membership.UserId });
        builder.Property(membership => membership.Role).HasConversion<string>().HasMaxLength(30);
        builder.Property(membership => membership.State).HasConversion<string>().HasMaxLength(30);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(membership => membership.WorkspaceId);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(membership => membership.UserId);
        builder.HasIndex(membership => new { membership.UserId, membership.State, membership.WorkspaceId });
    }
}

public sealed class WorkspaceAuditConfiguration : IEntityTypeConfiguration<WorkspaceAuditLog>
{
    public void Configure(EntityTypeBuilder<WorkspaceAuditLog> builder)
    {
        builder.ToTable("WorkspaceAuditLogs"); builder.HasKey(a => a.Id);
        builder.Property(a => a.Action).HasMaxLength(50).IsRequired();
        builder.Property(a => a.FromRole).HasMaxLength(30); builder.Property(a => a.ToRole).HasMaxLength(30);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(a => a.WorkspaceId);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.ActorUserId);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(a => a.TargetUserId);
        builder.HasIndex(a => new { a.WorkspaceId, a.CreatedAtUtc, a.Id });
    }
}
