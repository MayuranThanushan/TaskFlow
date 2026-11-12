using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class UserNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UserNotifications",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    RecipientUserId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    TaskId = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReadAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotifications", x => x.Id);
                    table.CheckConstraint("CK_UserNotifications_Kind", "Kind = 'TaskAssigned'");
                    table.ForeignKey(
                        name: "FK_UserNotifications_OutboxEvents_WorkspaceId_EventId",
                        columns: x => new { x.WorkspaceId, x.EventId },
                        principalTable: "OutboxEvents",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserNotifications_WorkspaceMemberships_WorkspaceId_Recipient~",
                        columns: x => new { x.WorkspaceId, x.RecipientUserId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_WorkspaceId_EventId",
                table: "UserNotifications",
                columns: new[] { "WorkspaceId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_WorkspaceId_RecipientUserId_EventId",
                table: "UserNotifications",
                columns: new[] { "WorkspaceId", "RecipientUserId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_WorkspaceId_RecipientUserId_Id",
                table: "UserNotifications",
                columns: new[] { "WorkspaceId", "RecipientUserId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_WorkspaceId_RecipientUserId_ReadAtUtc_Id",
                table: "UserNotifications",
                columns: new[] { "WorkspaceId", "RecipientUserId", "ReadAtUtc", "Id" });
            // Nullable compatibility-stage parent scope cannot be represented as an EF
            // alternate key without forcing existing legacy columns NOT NULL.
            migrationBuilder.CreateIndex("IX_UserNotifications_WorkspaceId_ProjectId_TaskId", "UserNotifications", new[] { "WorkspaceId", "ProjectId", "TaskId" });
            migrationBuilder.AddForeignKey("FK_UserNotifications_Projects_Scope", "UserNotifications", new[] { "WorkspaceId", "ProjectId" }, "Projects", principalColumns: new[] { "WorkspaceId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_UserNotifications_Tasks_Scope", "UserNotifications", new[] { "WorkspaceId", "ProjectId", "TaskId" }, "Tasks", principalColumns: new[] { "WorkspaceId", "ProjectId", "Id" }, onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserNotifications");
        }
    }
}
