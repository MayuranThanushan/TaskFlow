using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UserNotifications_Kind",
                table: "UserNotifications");

            migrationBuilder.AlterColumn<long>(
                name: "EventId",
                table: "UserNotifications",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AddColumn<string>(
                name: "ScheduleKey",
                table: "UserNotifications",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true,
                collation: "ascii_bin");

            migrationBuilder.AddColumn<DateTime>(
                name: "SourceDueDateUtc",
                table: "UserNotifications",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DailyDigestEnabled",
                table: "NotificationPreferences",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "DigestHour",
                table: "NotificationPreferences",
                type: "int",
                nullable: false,
                defaultValue: 9);

            migrationBuilder.AddColumn<bool>(
                name: "DueReminderEnabled",
                table: "NotificationPreferences",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "NotificationPreferences",
                type: "varchar(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.CreateTable(
                name: "NotificationScheduleCursors",
                columns: table => new
                {
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    LastTaskId = table.Column<long>(type: "bigint", nullable: false),
                    LastBatchAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationScheduleCursors", x => x.WorkspaceId);
                    table.ForeignKey(
                        name: "FK_NotificationScheduleCursors_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_WorkspaceId_RecipientUserId_ScheduleKey",
                table: "UserNotifications",
                columns: new[] { "WorkspaceId", "RecipientUserId", "ScheduleKey" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_UserNotifications_Kind",
                table: "UserNotifications",
                sql: "(Kind = 'TaskAssigned' AND EventId IS NOT NULL AND ScheduleKey IS NULL AND SourceDueDateUtc IS NULL) OR (Kind IN ('TaskDue', 'TaskDigest') AND EventId IS NULL AND ScheduleKey IS NOT NULL AND SourceDueDateUtc IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_NotificationPreferences_Hour",
                table: "NotificationPreferences",
                sql: "DigestHour BETWEEN 0 AND 23");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Scheduled notification history cannot be downgraded safely. Disable scheduling and roll forward, or restore a verified pre-release backup into a new schema.");
        }
    }
}
