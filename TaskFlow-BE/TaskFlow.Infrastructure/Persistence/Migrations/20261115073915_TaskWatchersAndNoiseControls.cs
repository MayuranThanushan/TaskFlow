using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskWatchersAndNoiseControls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UserNotifications_Kind",
                table: "UserNotifications");

            migrationBuilder.AddColumn<int>(
                name: "QuietEndHour",
                table: "NotificationPreferences",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuietStartHour",
                table: "NotificationPreferences",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WatcherDeliveryMode",
                table: "NotificationPreferences",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Immediate");

            migrationBuilder.CreateTable(
                name: "TaskWatchers",
                columns: table => new
                {
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    TaskId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    IsWatching = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskWatchers", x => new { x.WorkspaceId, x.TaskId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TaskWatchers_WorkspaceMemberships_WorkspaceId_UserId",
                        columns: x => new { x.WorkspaceId, x.UserId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "WatcherNotificationIntents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    TaskId = table.Column<long>(type: "bigint", nullable: false),
                    RecipientUserId = table.Column<long>(type: "bigint", nullable: false),
                    SubscriptionVersion = table.Column<long>(type: "bigint", nullable: false),
                    AuthorityWatermark = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<long>(type: "bigint", nullable: false),
                    CommentId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    State = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(24)", maxLength: 24, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WatcherNotificationIntents", x => x.Id);
                    table.CheckConstraint("CK_WatcherNotificationIntents_State", "(State = 'Pending' AND Outcome IS NULL AND CompletedAtUtc IS NULL) OR (State = 'Delivered' AND Outcome = 'InAppDelivered' AND CompletedAtUtc IS NOT NULL) OR (State = 'Suppressed' AND Outcome IN ('Expired','Unwatched','Revoked','Unavailable','Muted') AND CompletedAtUtc IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WatcherNotificationIntents_OutboxEvents_WorkspaceId_EventId",
                        columns: x => new { x.WorkspaceId, x.EventId },
                        principalTable: "OutboxEvents",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WatcherNotificationIntents_TaskComments_WorkspaceId_CommentId",
                        columns: x => new { x.WorkspaceId, x.CommentId },
                        principalTable: "TaskComments",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WatcherNotificationIntents_TaskWatchers_WorkspaceId_TaskId_R~",
                        columns: x => new { x.WorkspaceId, x.TaskId, x.RecipientUserId },
                        principalTable: "TaskWatchers",
                        principalColumns: new[] { "WorkspaceId", "TaskId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.AddCheckConstraint(
                name: "CK_UserNotifications_Kind",
                table: "UserNotifications",
                sql: "(Kind = 'TaskAssigned' AND EventId IS NOT NULL AND CommentId IS NULL AND ScheduleKey IS NULL AND SourceDueDateUtc IS NULL) OR (Kind = 'TaskWatched' AND EventId IS NOT NULL AND ScheduleKey IS NULL AND SourceDueDateUtc IS NULL) OR (Kind = 'TaskMention' AND EventId IS NOT NULL AND CommentId IS NOT NULL AND ScheduleKey IS NULL AND SourceDueDateUtc IS NULL) OR (Kind IN ('TaskDue', 'TaskDigest') AND EventId IS NULL AND CommentId IS NULL AND ScheduleKey IS NOT NULL AND SourceDueDateUtc IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationPreferences_Noise",
                table: "NotificationPreferences",
                sql: "WatcherDeliveryMode IN ('Immediate','Digest','Off') AND ((QuietStartHour IS NULL AND QuietEndHour IS NULL) OR (QuietStartHour IS NOT NULL AND QuietEndHour IS NOT NULL AND QuietStartHour BETWEEN 0 AND 23 AND QuietEndHour BETWEEN 0 AND 23 AND QuietStartHour <> QuietEndHour))");

            migrationBuilder.CreateIndex(
                name: "IX_TaskWatchers_WorkspaceId_UserId",
                table: "TaskWatchers",
                columns: new[] { "WorkspaceId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatcherNotificationIntents_WorkspaceId_CommentId",
                table: "WatcherNotificationIntents",
                columns: new[] { "WorkspaceId", "CommentId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatcherNotificationIntents_WorkspaceId_EventId",
                table: "WatcherNotificationIntents",
                columns: new[] { "WorkspaceId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_WatcherNotificationIntents_WorkspaceId_RecipientUserId_Event~",
                table: "WatcherNotificationIntents",
                columns: new[] { "WorkspaceId", "RecipientUserId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WatcherNotificationIntents_WorkspaceId_State_AvailableAtUtc_~",
                table: "WatcherNotificationIntents",
                columns: new[] { "WorkspaceId", "State", "AvailableAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WatcherNotificationIntents_WorkspaceId_TaskId_RecipientUserId",
                table: "WatcherNotificationIntents",
                columns: new[] { "WorkspaceId", "TaskId", "RecipientUserId" });
            // Legacy nullable task scope requires explicit composite constraints.
            migrationBuilder.CreateIndex("IX_TaskWatchers_TaskScope", "TaskWatchers", new[] { "WorkspaceId", "ProjectId", "TaskId" });
            migrationBuilder.AddForeignKey("FK_TaskWatchers_Tasks_Scope", "TaskWatchers", new[] { "WorkspaceId", "ProjectId", "TaskId" }, "Tasks", principalColumns: new[] { "WorkspaceId", "ProjectId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.CreateIndex("IX_WatcherNotificationIntents_TaskScope", "WatcherNotificationIntents", new[] { "WorkspaceId", "ProjectId", "TaskId" });
            migrationBuilder.AddForeignKey("FK_WatcherNotificationIntents_Tasks_Scope", "WatcherNotificationIntents", new[] { "WorkspaceId", "ProjectId", "TaskId" }, "Tasks", principalColumns: new[] { "WorkspaceId", "ProjectId", "Id" }, onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Watcher subscriptions and delivery history cannot be downgraded safely. Disable the feature and roll forward, or restore a verified backup to a new schema.");
        }
    }
}
