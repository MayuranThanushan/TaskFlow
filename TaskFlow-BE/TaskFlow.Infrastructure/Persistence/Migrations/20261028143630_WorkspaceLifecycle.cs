using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkspaceMemberships_State",
                table: "WorkspaceMemberships");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProjectMembers_Role",
                table: "ProjectMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActivityLogs_ActionType",
                table: "ActivityLogs");

            migrationBuilder.AddColumn<DateTime>(
                name: "RevokedAtUtc",
                table: "ProjectMembers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WorkspaceAuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    TargetUserId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    Action = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false),
                    FromRole = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    ToRole = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceAuditLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkspaceAuditLogs_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceAuditLogs_AspNetUsers_TargetUserId",
                        column: x => x.TargetUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceAuditLogs_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkspaceMemberships_State",
                table: "WorkspaceMemberships",
                sql: "`State` IN ('Active','Suspended','Removed')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProjectMembers_Role",
                table: "ProjectMembers",
                sql: "`Role` IN ('Member','Editor','Viewer')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActivityLogs_ActionType",
                table: "ActivityLogs",
                sql: "`ActionType` IN ('ProjectCreated','ProjectUpdated','ProjectDeleted','MemberAdded','MemberRemoved','TaskCreated','TaskUpdated','TaskAssigned','TaskUnassigned','TaskStatusChanged','TaskDeleted','MemberRoleChanged','ProjectOwnershipTransferred')");

            // Scope references mirror the compatibility-stage project constraints.
            migrationBuilder.AddForeignKey("FK_WorkspaceAuditLogs_Members_Actor", "WorkspaceAuditLogs", new[] { "WorkspaceId", "ActorUserId" }, "WorkspaceMemberships", principalColumns: new[] { "WorkspaceId", "UserId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_WorkspaceAuditLogs_Members_Target", "WorkspaceAuditLogs", new[] { "WorkspaceId", "TargetUserId" }, "WorkspaceMemberships", principalColumns: new[] { "WorkspaceId", "UserId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_WorkspaceAuditLogs_Projects_Scope", "WorkspaceAuditLogs", new[] { "WorkspaceId", "ProjectId" }, "Projects", principalColumns: new[] { "WorkspaceId", "Id" }, onDelete: ReferentialAction.Restrict);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAuditLogs_ActorUserId",
                table: "WorkspaceAuditLogs",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAuditLogs_TargetUserId",
                table: "WorkspaceAuditLogs",
                column: "TargetUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceAuditLogs_WorkspaceId_CreatedAtUtc_Id",
                table: "WorkspaceAuditLogs",
                columns: new[] { "WorkspaceId", "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkspaceAuditLogs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkspaceMemberships_State",
                table: "WorkspaceMemberships");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ProjectMembers_Role",
                table: "ProjectMembers");

            migrationBuilder.DropCheckConstraint(
                name: "CK_ActivityLogs_ActionType",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "RevokedAtUtc",
                table: "ProjectMembers");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkspaceMemberships_State",
                table: "WorkspaceMemberships",
                sql: "`State` IN ('Active','Suspended')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ProjectMembers_Role",
                table: "ProjectMembers",
                sql: "`Role` = 'Member'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_ActivityLogs_ActionType",
                table: "ActivityLogs",
                sql: "`ActionType` IN ('ProjectCreated','ProjectUpdated','ProjectDeleted','MemberAdded','MemberRemoved','TaskCreated','TaskUpdated','TaskAssigned','TaskUnassigned','TaskStatusChanged','TaskDeleted')");
        }
    }
}
