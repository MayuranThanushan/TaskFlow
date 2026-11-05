using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WorkspaceInvitations",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    InviterUserId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    Email = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: false),
                    PendingEmail = table.Column<string>(type: "varchar(254)", maxLength: 254, nullable: true),
                    Role = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    State = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LastSentAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Resends = table.Column<int>(type: "int", nullable: false),
                    AcceptedUserId = table.Column<long>(type: "bigint", nullable: true),
                    MessageId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    ProtectedMessage = table.Column<string>(type: "text", nullable: true),
                    DeliveryState = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    LeaseToken = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastCode = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkspaceInvitations", x => x.Id);
                    table.UniqueConstraint("AK_WorkspaceInvitations_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_WorkspaceInvitations_AspNetUsers_AcceptedUserId",
                        column: x => x.AcceptedUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkspaceInvitations_WorkspaceMemberships_WorkspaceId_Invite~",
                        columns: x => new { x.WorkspaceId, x.InviterUserId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "InvitationTransitions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    InvitationId = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InvitationTransitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InvitationTransitions_AspNetUsers_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InvitationTransitions_WorkspaceInvitations_WorkspaceId_Invit~",
                        columns: x => new { x.WorkspaceId, x.InvitationId },
                        principalTable: "WorkspaceInvitations",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_InvitationTransitions_ActorUserId",
                table: "InvitationTransitions",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_InvitationTransitions_WorkspaceId_InvitationId_Id",
                table: "InvitationTransitions",
                columns: new[] { "WorkspaceId", "InvitationId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_AcceptedUserId",
                table: "WorkspaceInvitations",
                column: "AcceptedUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_DeliveryState_AvailableAtUtc_Id",
                table: "WorkspaceInvitations",
                columns: new[] { "DeliveryState", "AvailableAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_MessageId",
                table: "WorkspaceInvitations",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_WorkspaceId_CreatedAtUtc",
                table: "WorkspaceInvitations",
                columns: new[] { "WorkspaceId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_WorkspaceId_InviterUserId",
                table: "WorkspaceInvitations",
                columns: new[] { "WorkspaceId", "InviterUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_WorkspaceId_PendingEmail",
                table: "WorkspaceInvitations",
                columns: new[] { "WorkspaceId", "PendingEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkspaceInvitations_WorkspaceId_ProjectId",
                table: "WorkspaceInvitations",
                columns: new[] { "WorkspaceId", "ProjectId" });
            migrationBuilder.Sql("ALTER TABLE WorkspaceInvitations ADD CONSTRAINT FK_WorkspaceInvitations_Projects_Scope FOREIGN KEY (WorkspaceId, ProjectId) REFERENCES Projects (WorkspaceId, Id) ON DELETE RESTRICT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InvitationTransitions");

            migrationBuilder.DropTable(
                name: "WorkspaceInvitations");
        }
    }
}
