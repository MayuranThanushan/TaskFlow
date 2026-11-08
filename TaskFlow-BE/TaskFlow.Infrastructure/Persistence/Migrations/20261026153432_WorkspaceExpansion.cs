using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceExpansion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "WorkspaceId",
                table: "Tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WorkspaceId",
                table: "Projects",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WorkspaceId",
                table: "ProjectMembers",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WorkspaceId",
                table: "ActivityLogs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Workspaces",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    Name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: false),
                    LegacyOwnerId = table.Column<long>(type: "bigint", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workspaces", x => x.Id);
                    table.CheckConstraint("CK_Workspaces_Name", "CHAR_LENGTH(TRIM(`Name`)) > 0");
                    table.ForeignKey(
                        name: "FK_Workspaces_AspNetUsers_LegacyOwnerId",
                        column: x => x.LegacyOwnerId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "WorkspaceMemberships",
                columns: table => new
                {
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Role = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    JoinedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceMemberships", x => new { x.WorkspaceId, x.UserId });
                    table.CheckConstraint("CK_WorkspaceMemberships_Role", "`Role` IN ('Owner','Admin','Member','Guest')");
                    table.CheckConstraint("CK_WorkspaceMemberships_State", "`State` IN ('Active','Suspended')");
                    table.ForeignKey(
                        name: "FK_WorkspaceMemberships_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceMemberships_Workspaces_WorkspaceId",
                        column: x => x.WorkspaceId,
                        principalTable: "Workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkspaceId_ProjectId_Id",
                table: "Tasks",
                columns: new[] { "WorkspaceId", "ProjectId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkspaceId_ProjectId_Status",
                table: "Tasks",
                columns: new[] { "WorkspaceId", "ProjectId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Projects_WorkspaceId_Id",
                table: "Projects",
                columns: new[] { "WorkspaceId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProjectMembers_WorkspaceId_UserId_ProjectId",
                table: "ProjectMembers",
                columns: new[] { "WorkspaceId", "UserId", "ProjectId" });

            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_WorkspaceId_ProjectId_CreatedAtUtc_Id",
                table: "ActivityLogs",
                columns: new[] { "WorkspaceId", "ProjectId", "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceMemberships_UserId_State_WorkspaceId",
                table: "WorkspaceMemberships",
                columns: new[] { "UserId", "State", "WorkspaceId" });

            migrationBuilder.CreateIndex(
                name: "IX_Workspaces_LegacyOwnerId",
                table: "Workspaces",
                column: "LegacyOwnerId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ActivityLogs_Workspaces_WorkspaceId",
                table: "ActivityLogs",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectMembers_Workspaces_WorkspaceId",
                table: "ProjectMembers",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Workspaces_WorkspaceId",
                table: "Projects",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Workspaces_WorkspaceId",
                table: "Tasks",
                column: "WorkspaceId",
                principalTable: "Workspaces",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityLogs_Workspaces_WorkspaceId",
                table: "ActivityLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectMembers_Workspaces_WorkspaceId",
                table: "ProjectMembers");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Workspaces_WorkspaceId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Workspaces_WorkspaceId",
                table: "Tasks");

            migrationBuilder.DropTable(
                name: "WorkspaceMemberships");

            migrationBuilder.DropTable(
                name: "Workspaces");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkspaceId_ProjectId_Id",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_WorkspaceId_ProjectId_Status",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Projects_WorkspaceId_Id",
                table: "Projects");

            migrationBuilder.DropIndex(
                name: "IX_ProjectMembers_WorkspaceId_UserId_ProjectId",
                table: "ProjectMembers");

            migrationBuilder.DropIndex(
                name: "IX_ActivityLogs_WorkspaceId_ProjectId_CreatedAtUtc_Id",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "ProjectMembers");

            migrationBuilder.DropColumn(
                name: "WorkspaceId",
                table: "ActivityLogs");
        }
    }
}
