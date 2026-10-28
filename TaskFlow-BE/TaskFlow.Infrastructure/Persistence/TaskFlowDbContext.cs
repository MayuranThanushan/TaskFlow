using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;

namespace TaskFlow.Infrastructure.Persistence;

public sealed class TaskFlowDbContext(DbContextOptions<TaskFlowDbContext> options, TimeProvider clock)
    : IdentityDbContext<ApplicationUser, IdentityRole<long>, long>(options)
{
    public DbSet<WorkspaceAuditLog> WorkspaceAuditLogs => Set<WorkspaceAuditLog>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMembership> WorkspaceMemberships => Set<WorkspaceMembership>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<ActivityLog> ActivityLogs => Set<ActivityLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>().HaveColumnType("datetime(6)");
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseCollation("utf8mb4_0900_ai_ci");
        builder.ApplyConfigurationsFromAssembly(typeof(TaskFlowDbContext).Assembly);
        foreach (var foreignKey in builder.Model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()))
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareChanges();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareChanges();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void PrepareChanges()
    {
        ChangeTracker.DetectChanges();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is (ActivityLog or WorkspaceAuditLog) && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Activity history is append-only.");
            if (entry.State == EntityState.Deleted && entry.Entity is Project or TaskItem)
                throw new InvalidOperationException("Projects and tasks require explicit soft deletion.");
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            if (entry.Entity is WorkspaceAuditLog audit && entry.State == EntityState.Added) audit.CreatedAtUtc = now;
            switch (entry.Entity)
            {
                case Workspace workspace:
                    workspace.Name = TextRules.Required(workspace.Name, 150, nameof(workspace.Name));
                    break;
                case WorkspaceMembership membership:
                    if (!Enum.IsDefined(membership.Role) || !Enum.IsDefined(membership.State))
                        throw new ArgumentException("Unsupported workspace membership.");
                    if (entry.State == EntityState.Added) membership.JoinedAtUtc = now;
                    break;
                case Project project:
                    project.Name = TextRules.Required(project.Name, 150, nameof(project.Name));
                    project.Description = TextRules.Optional(project.Description, 10_000, nameof(project.Description));
                    break;
                case TaskItem task:
                    task.Title = TextRules.Required(task.Title, 200, nameof(task.Title));
                    task.Description = TextRules.Optional(task.Description, 10_000, nameof(task.Description));
                    if (!Enum.IsDefined(task.Status) || !Enum.IsDefined(task.Priority))
                        throw new ArgumentException("Unsupported task status or priority.");
                    break;
                case ApplicationUser user:
                    user.DisplayName = TextRules.Required(user.DisplayName, 100, nameof(user.DisplayName));
                    if (string.IsNullOrWhiteSpace(user.Email) || string.IsNullOrWhiteSpace(user.NormalizedEmail))
                        throw new ArgumentException("Email and normalized email are required.");
                    break;
                case ProjectMember member:
                    if (!Enum.IsDefined(member.Role)) throw new ArgumentException("Unsupported membership role.");
                    if (entry.State == EntityState.Added) member.JoinedAtUtc = now;
                    break;
                case ActivityLog activity:
                    if (!Enum.IsDefined(activity.ActionType)) throw new ArgumentException("Unsupported activity action.");
                    activity.CreatedAtUtc = now;
                    break;
            }

            if (entry.Entity is IAuditable auditable)
            {
                if (entry.State == EntityState.Added) auditable.CreatedAtUtc = now;
                else entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                auditable.UpdatedAtUtc = now;
            }
        }
    }
}
