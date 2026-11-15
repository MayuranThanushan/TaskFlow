using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BoardRank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BoardRank",
                table: "Tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.DropCheckConstraint(name: "CK_ActivityLogs_ActionType", table: "ActivityLogs");
            migrationBuilder.AddCheckConstraint(name: "CK_ActivityLogs_ActionType", table: "ActivityLogs",
                sql: "`ActionType` IN ('ProjectCreated','ProjectUpdated','ProjectDeleted','MemberAdded','MemberRemoved','TaskCreated','TaskUpdated','TaskAssigned','TaskUnassigned','TaskStatusChanged','TaskDeleted','MemberRoleChanged','ProjectOwnershipTransferred','TaskMoved','TaskRankRebalanced')");
            // Preserve the previous per-column created-date/ID descending order,
            // including retained rows, without fabricating business events or revisions.
            migrationBuilder.Sql("""
                UPDATE Tasks t
                JOIN (SELECT Id, ROW_NUMBER() OVER (PARTITION BY ProjectId, Status ORDER BY CreatedAtUtc DESC, Id DESC) * 1024 AS NewRank FROM Tasks) ranked ON ranked.Id=t.Id
                SET t.BoardRank=ranked.NewRank
                """);
            migrationBuilder.CreateIndex(
                name: "IX_Tasks_WorkspaceId_ProjectId_Status_BoardRank_Id",
                table: "Tasks",
                columns: new[] { "WorkspaceId", "ProjectId", "Status", "BoardRank", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new System.InvalidOperationException("Board ordering and move history must be preserved. Disable moves and roll forward or restore a verified pre-release backup into a new schema.");
        }
    }
}
