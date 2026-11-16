using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SavedViews : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SavedViews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false),
                    Surface = table.Column<string>(type: "varchar(12)", maxLength: 12, nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    Shared = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    FiltersJson = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedViews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedViews_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SavedViews_WorkspaceMemberships_WorkspaceId_UserId",
                        columns: x => new { x.WorkspaceId, x.UserId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "ViewPreferences",
                columns: table => new
                {
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Context = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    SavedViewId = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ViewPreferences", x => new { x.WorkspaceId, x.UserId, x.Context });
                    table.ForeignKey(
                        name: "FK_ViewPreferences_WorkspaceMemberships_WorkspaceId_UserId",
                        columns: x => new { x.WorkspaceId, x.UserId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_SavedViews_ProjectId",
                table: "SavedViews",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_SavedViews_WorkspaceId_Shared_Id",
                table: "SavedViews",
                columns: new[] { "WorkspaceId", "Shared", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_SavedViews_WorkspaceId_UserId_Id",
                table: "SavedViews",
                columns: new[] { "WorkspaceId", "UserId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SavedViews");

            migrationBuilder.DropTable(
                name: "ViewPreferences");
        }
    }
}
