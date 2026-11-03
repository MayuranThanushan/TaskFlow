using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TransactionalOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OutboxEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    MessageId = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: true),
                    TaskId = table.Column<long>(type: "bigint", nullable: true),
                    SchemaVersion = table.Column<int>(type: "int", nullable: false),
                    Kind = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false),
                    CorrelationId = table.Column<string>(type: "varchar(55)", maxLength: 55, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxEvents", x => x.Id);
                    table.UniqueConstraint("AK_OutboxEvents_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_OutboxEvents_TaskParent", "TaskId IS NULL OR ProjectId IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_OutboxEvents_WorkspaceMemberships_WorkspaceId_ActorUserId",
                        columns: x => new { x.WorkspaceId, x.ActorUserId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "OutboxDeliveries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    EventId = table.Column<long>(type: "bigint", nullable: false),
                    Consumer = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false),
                    State = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    RoundAttempts = table.Column<int>(type: "int", nullable: false),
                    Replays = table.Column<int>(type: "int", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP(6)"),
                    LeaseToken = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    LastFailureCode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxDeliveries", x => x.Id);
                    table.UniqueConstraint("AK_OutboxDeliveries_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.ForeignKey(
                        name: "FK_OutboxDeliveries_OutboxEvents_WorkspaceId_EventId",
                        columns: x => new { x.WorkspaceId, x.EventId },
                        principalTable: "OutboxEvents",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "OutboxAttempts",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: false),
                    Attempt = table.Column<int>(type: "int", nullable: false),
                    Outcome = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false),
                    FailureCode = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxAttempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OutboxAttempts_OutboxDeliveries_WorkspaceId_DeliveryId",
                        columns: x => new { x.WorkspaceId, x.DeliveryId },
                        principalTable: "OutboxDeliveries",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxAttempts_DeliveryId_Attempt_Outcome",
                table: "OutboxAttempts",
                columns: new[] { "DeliveryId", "Attempt", "Outcome" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxAttempts_WorkspaceId_DeliveryId",
                table: "OutboxAttempts",
                columns: new[] { "WorkspaceId", "DeliveryId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_EventId_Consumer",
                table: "OutboxDeliveries",
                columns: new[] { "EventId", "Consumer" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_State_AvailableAtUtc_Id",
                table: "OutboxDeliveries",
                columns: new[] { "State", "AvailableAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_State_LeaseExpiresAtUtc_Id",
                table: "OutboxDeliveries",
                columns: new[] { "State", "LeaseExpiresAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_WorkspaceId_EventId",
                table: "OutboxDeliveries",
                columns: new[] { "WorkspaceId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_MessageId",
                table: "OutboxEvents",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxEvents_WorkspaceId_ActorUserId",
                table: "OutboxEvents",
                columns: new[] { "WorkspaceId", "ActorUserId" });
            migrationBuilder.Sql("ALTER TABLE `OutboxEvents` ADD CONSTRAINT `FK_OutboxEvents_Projects_Scope` FOREIGN KEY (`WorkspaceId`,`ProjectId`) REFERENCES `Projects` (`WorkspaceId`,`Id`) ON DELETE RESTRICT");
            migrationBuilder.Sql("ALTER TABLE `OutboxEvents` ADD CONSTRAINT `FK_OutboxEvents_Tasks_Scope` FOREIGN KEY (`WorkspaceId`,`ProjectId`,`TaskId`) REFERENCES `Tasks` (`WorkspaceId`,`ProjectId`,`Id`) ON DELETE RESTRICT");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OutboxAttempts");

            migrationBuilder.DropTable(
                name: "OutboxDeliveries");

            migrationBuilder.DropTable(
                name: "OutboxEvents");
        }
    }
}
