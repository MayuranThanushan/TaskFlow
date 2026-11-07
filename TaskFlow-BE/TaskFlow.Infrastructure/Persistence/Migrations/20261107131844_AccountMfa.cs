using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountMfa : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AccountMfa",
                columns: table => new
                {
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    ProtectedSecret = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true),
                    PendingProtectedSecret = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true),
                    PendingExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastAcceptedStep = table.Column<long>(type: "bigint", nullable: false),
                    RecoveryHashes = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AccountMfa", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_AccountMfa_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AccountMfa");
        }
    }
}
