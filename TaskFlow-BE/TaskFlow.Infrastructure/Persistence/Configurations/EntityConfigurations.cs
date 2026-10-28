using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;

namespace TaskFlow.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(user => user.DisplayName).HasMaxLength(100).IsRequired();
        builder.Property(user => user.Email).HasMaxLength(256).IsRequired();
        builder.Property(user => user.NormalizedEmail).HasMaxLength(256).IsRequired();
        builder.HasIndex(user => user.NormalizedEmail).HasDatabaseName("EmailIndex").IsUnique();
    }
}

public sealed class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects", table => table.HasCheckConstraint("CK_Projects_Name", "CHAR_LENGTH(TRIM(`Name`)) > 0"));
        builder.HasKey(project => project.Id);
        builder.HasIndex(project => new { project.WorkspaceId, project.Id }).IsUnique();
        builder.HasOne<Workspace>().WithMany().HasForeignKey(project => project.WorkspaceId);
        builder.Property(project => project.Name).HasMaxLength(150).IsRequired();
        builder.Property(project => project.Description).HasColumnType("text");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(project => project.OwnerId);
        builder.HasQueryFilter(project => project.DeletedAtUtc == null);
    }
}

public sealed class MemberConfiguration : IEntityTypeConfiguration<ProjectMember>
{
    public void Configure(EntityTypeBuilder<ProjectMember> builder)
    {
        builder.ToTable("ProjectMembers", table => table.HasCheckConstraint("CK_ProjectMembers_Role", "`Role` IN ('Member','Editor','Viewer')"));
        builder.HasKey(member => new { member.ProjectId, member.UserId });
        builder.HasOne<Workspace>().WithMany().HasForeignKey(member => member.WorkspaceId);
        builder.HasIndex(member => new { member.UserId, member.ProjectId });
        builder.Property(member => member.Role).HasConversion<string>().HasMaxLength(30);
        builder.HasOne(member => member.Project).WithMany().HasForeignKey(member => member.ProjectId);
        builder.HasIndex(member => new { member.WorkspaceId, member.UserId, member.ProjectId });
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(member => member.UserId);
        builder.HasQueryFilter(member => member.Project.DeletedAtUtc == null);
    }
}

public sealed class TaskConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> builder)
    {
        builder.ToTable("Tasks", table =>
        {
            table.HasCheckConstraint("CK_Tasks_Status", "`Status` IN ('ToDo','InProgress','Done')");
            table.HasCheckConstraint("CK_Tasks_Priority", "`Priority` IN ('Low','Medium','High')");
            table.HasCheckConstraint("CK_Tasks_Title", "CHAR_LENGTH(TRIM(`Title`)) > 0");
        });
        builder.HasKey(task => task.Id);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(task => task.WorkspaceId);
        builder.HasIndex(task => new { task.WorkspaceId, task.ProjectId, task.Id }).IsUnique();
        builder.Property(task => task.Title).HasMaxLength(200).IsRequired();
        builder.Property(task => task.Description).HasColumnType("text");
        builder.Property(task => task.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(task => task.Priority).HasConversion<string>().HasMaxLength(20);
        builder.HasOne(task => task.Project).WithMany().HasForeignKey(task => task.ProjectId);
        builder.HasIndex(task => new { task.WorkspaceId, task.ProjectId, task.Status });
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(task => task.AssigneeId);
        builder.HasIndex(task => new { task.ProjectId, task.Status });
        builder.HasIndex(task => new { task.ProjectId, task.DueDateUtc });
        builder.HasQueryFilter(task => task.DeletedAtUtc == null && task.Project.DeletedAtUtc == null);
    }
}

public sealed class ActivityConfiguration : IEntityTypeConfiguration<ActivityLog>
{
    public void Configure(EntityTypeBuilder<ActivityLog> builder)
    {
        builder.ToTable("ActivityLogs", table => table.HasCheckConstraint("CK_ActivityLogs_ActionType",
            "`ActionType` IN ('ProjectCreated','ProjectUpdated','ProjectDeleted','MemberAdded','MemberRemoved','TaskCreated','TaskUpdated','TaskAssigned','TaskUnassigned','TaskStatusChanged','TaskDeleted','MemberRoleChanged','ProjectOwnershipTransferred')"));
        builder.HasKey(activity => activity.Id);
        builder.HasOne<Workspace>().WithMany().HasForeignKey(activity => activity.WorkspaceId);
        builder.Property(activity => activity.ActionType).HasConversion<string>().HasMaxLength(50);
        builder.Property(activity => activity.Details).HasColumnType("text");
        builder.HasOne(activity => activity.Project).WithMany().HasForeignKey(activity => activity.ProjectId);
        builder.HasOne(activity => activity.Task).WithMany().HasForeignKey(activity => activity.TaskId);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(activity => activity.ActorUserId);
        builder.HasIndex(activity => new { activity.ProjectId, activity.CreatedAtUtc, activity.Id });
        builder.HasIndex(activity => new { activity.WorkspaceId, activity.ProjectId, activity.CreatedAtUtc, activity.Id });
        builder.HasQueryFilter(activity => activity.Project.DeletedAtUtc == null);
    }
}
