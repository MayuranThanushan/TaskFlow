using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountSecurityMail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountEmailJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    MessageId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ProtectedPayload = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LeaseToken = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountEmailJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AccountEmailJobs_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_AccountEmailJobs_MessageId",
                table: "AccountEmailJobs",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AccountEmailJobs_State_AvailableAtUtc_Id",
                table: "AccountEmailJobs",
                columns: new[] { "State", "AvailableAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountEmailJobs_UserId_CreatedAtUtc",
                table: "AccountEmailJobs",
                columns: new[] { "UserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountEmailJobs");
        }
    }
}
