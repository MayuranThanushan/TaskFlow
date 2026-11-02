using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PilotPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AuditExportsEnabled",
                table: "Workspaces",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "AuditRetentionDays",
                table: "Workspaces",
                type: "int",
                nullable: false,
                defaultValue: 365);

            migrationBuilder.AddColumn<int>(
                name: "DeletedContentRetentionDays",
                table: "Workspaces",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<bool>(
                name: "LegalHold",
                table: "Workspaces",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<long>(
                name: "PolicyVersion",
                table: "Workspaces",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "PilotGates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PilotGates", x => x.Id);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.InsertData(
                table: "PilotGates",
                column: "Id",
                value: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PilotGates");

            migrationBuilder.DropColumn(
                name: "AuditExportsEnabled",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "AuditRetentionDays",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "DeletedContentRetentionDays",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "LegalHold",
                table: "Workspaces");

            migrationBuilder.DropColumn(
                name: "PolicyVersion",
                table: "Workspaces");
        }
    }
}
