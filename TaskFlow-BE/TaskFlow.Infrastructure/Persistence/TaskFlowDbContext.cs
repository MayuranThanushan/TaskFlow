using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;

namespace TaskFlow.Infrastructure.Persistence;

public sealed class TaskFlowDbContext(DbContextOptions<TaskFlowDbContext> options, TimeProvider clock, IConfiguration? configuration = null)
    : IdentityDbContext<ApplicationUser, IdentityRole<long>, long>(options)
{
    private readonly AuditCapture auditCapture = new();
    public DbSet<NotificationScheduleCursor> NotificationScheduleCursors => Set<NotificationScheduleCursor>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();
    public bool NotificationsEnabled => configuration?.GetValue<bool>("Notifications:Enabled") == true;
    public DbSet<AccountMfa> AccountMfa => Set<AccountMfa>();
    public DbSet<WorkspaceInvitation> WorkspaceInvitations => Set<WorkspaceInvitation>();
    public DbSet<InvitationTransition> InvitationTransitions => Set<InvitationTransition>();
    public DbSet<AccountEmailJob> AccountEmailJobs => Set<AccountEmailJob>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<OutboxDelivery> OutboxDeliveries => Set<OutboxDelivery>();
    public DbSet<OutboxAttempt> OutboxAttempts => Set<OutboxAttempt>();
    public DbSet<PilotGate> PilotGates => Set<PilotGate>();
    public DbSet<SecurityEvent> SecurityEvents => Set<SecurityEvent>();
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
        builder.Entity<AccountMfa>().HasKey(m => m.UserId);
        builder.Entity<AccountMfa>().HasOne<ApplicationUser>().WithOne().HasForeignKey<AccountMfa>(m => m.UserId);
        builder.Entity<AccountMfa>().Property(m => m.ProtectedSecret).HasMaxLength(2048);
        builder.Entity<AccountMfa>().Property(m => m.PendingProtectedSecret).HasMaxLength(2048);
        builder.Entity<AccountMfa>().Property(m => m.RecoveryHashes).HasMaxLength(2048);
        builder.Entity<PilotGate>().HasKey(g => g.Id);
        builder.Entity<PilotGate>().HasData(new PilotGate { Id = 1 });
        builder.Entity<Workspace>().Property(w => w.PolicyVersion).HasDefaultValue(1L).IsConcurrencyToken();
        builder.Entity<Workspace>().Property(w => w.AuditRetentionDays).HasDefaultValue(365);
        builder.Entity<Workspace>().Property(w => w.DeletedContentRetentionDays).HasDefaultValue(30);
        builder.Entity<Workspace>().Property(w => w.LegalHold).HasDefaultValue(true).HasSentinel(true);
        builder.Entity<Workspace>().Property(w => w.AuditExportsEnabled).HasDefaultValue(true).HasSentinel(true);
        builder.Entity<Project>().Property(p => p.Version).IsConcurrencyToken().HasDefaultValue(1L);
        builder.Entity<TaskItem>().Property(t => t.Version).IsConcurrencyToken().HasDefaultValue(1L);
        foreach (var foreignKey in builder.Model.GetEntityTypes().SelectMany(entity => entity.GetForeignKeys()))
            foreignKey.DeleteBehavior = DeleteBehavior.Restrict;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PrepareChanges();
        var hadAudit = ChangeTracker.Entries().Any(e => e.State == EntityState.Added && e.Entity is IVersionedAudit);
        var result = base.SaveChanges(acceptAllChangesOnSuccess);
        auditCapture.Saved(hadAudit); return result;
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PrepareChanges();
        var hadAudit = ChangeTracker.Entries().Any(e => e.State == EntityState.Added && e.Entity is IVersionedAudit);
        var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        auditCapture.Saved(hadAudit); return result;
    }

    private void PrepareChanges()
    {
        ChangeTracker.DetectChanges();
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var entry in ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is (ActivityLog or WorkspaceAuditLog or SecurityEvent or OutboxEvent or OutboxAttempt or InvitationTransition) && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException("Activity history is append-only.");
            if (entry.Entity is UserNotification && (entry.State == EntityState.Deleted || entry.State == EntityState.Modified && entry.Properties.Any(p => p.IsModified && p.Metadata.Name != nameof(UserNotification.ReadAtUtc))))
                throw new InvalidOperationException("Only notification read state is mutable.");
            if (entry.State == EntityState.Deleted && entry.Entity is Project or TaskItem)
                throw new InvalidOperationException("Projects and tasks require explicit soft deletion.");
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;

            if (entry.Entity is WorkspaceAuditLog audit && entry.State == EntityState.Added) audit.CreatedAtUtc = now;
            if (entry.Entity is SecurityEvent security && entry.State == EntityState.Added) security.CreatedAtUtc = now;
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

            if (entry.Entity is IVersionedResource resource)
            {
                if (entry.State == EntityState.Added) resource.Version = 1;
                else
                {
                    var original = (long)entry.Property(nameof(IVersionedResource.Version)).OriginalValue!;
                    if (original == long.MaxValue) throw new TaskFlow.Application.Features.Projects.ResourceVersionException(409, "resource.version_exhausted");
                    resource.Version = checked(original + 1);
                }
            }
            if (entry.Entity is IAuditable auditable)
            {
                if (entry.State == EntityState.Added) auditable.CreatedAtUtc = now;
                else entry.Property(nameof(IAuditable.CreatedAtUtc)).IsModified = false;
                auditable.UpdatedAtUtc = now;
            }
        }
        ChangeTracker.DetectChanges();
        auditCapture.Prepare(this);
    }
}
