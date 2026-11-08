using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StructuredAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ChangesJson",
                table: "WorkspaceAuditLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "WorkspaceAuditLogs",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "WorkspaceAuditLogs",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchemaVersion",
                table: "WorkspaceAuditLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "WorkspaceAuditLogs",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectType",
                table: "WorkspaceAuditLogs",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ChangesJson",
                table: "ActivityLogs",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "ActivityLogs",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Outcome",
                table: "ActivityLogs",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchemaVersion",
                table: "ActivityLogs",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                table: "ActivityLogs",
                type: "varchar(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectType",
                table: "ActivityLogs",
                type: "varchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "SecurityEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Action = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    Outcome = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: true),
                    SubjectUserId = table.Column<long>(type: "bigint", nullable: true),
                    SubjectType = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    RouteTemplate = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    Coalesced = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityEvents", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_CreatedAtUtc_Id",
                table: "SecurityEvents",
                columns: new[] { "CreatedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityEvents_SubjectUserId_CreatedAtUtc_Id",
                table: "SecurityEvents",
                columns: new[] { "SubjectUserId", "CreatedAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SecurityEvents");

            migrationBuilder.DropColumn(
                name: "ChangesJson",
                table: "WorkspaceAuditLogs");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "WorkspaceAuditLogs");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "WorkspaceAuditLogs");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                table: "WorkspaceAuditLogs");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "WorkspaceAuditLogs");

            migrationBuilder.DropColumn(
                name: "SubjectType",
                table: "WorkspaceAuditLogs");

            migrationBuilder.DropColumn(
                name: "ChangesJson",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                table: "ActivityLogs");

            migrationBuilder.DropColumn(
                name: "SubjectType",
                table: "ActivityLogs");
        }
    }
}
