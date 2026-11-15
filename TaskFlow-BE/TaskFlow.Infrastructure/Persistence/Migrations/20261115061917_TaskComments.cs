using System;
using Microsoft.EntityFrameworkCore.Migrations;
using MySql.EntityFrameworkCore.Metadata;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TaskComments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_UserNotifications_Kind",
                table: "UserNotifications");

            migrationBuilder.AddColumn<long>(
                name: "CommentId",
                table: "UserNotifications",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskComments",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    ProjectId = table.Column<long>(type: "bigint", nullable: false),
                    TaskId = table.Column<long>(type: "bigint", nullable: false),
                    AuthorId = table.Column<long>(type: "bigint", nullable: false),
                    Body = table.Column<string>(type: "varchar(5000)", maxLength: 5000, nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EditedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    DeletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskComments", x => x.Id);
                    table.UniqueConstraint("AK_TaskComments_WorkspaceId_Id", x => new { x.WorkspaceId, x.Id });
                    table.CheckConstraint("CK_TaskComments_Body", "(DeletedAtUtc IS NULL AND CHAR_LENGTH(TRIM(Body)) BETWEEN 1 AND 5000) OR (DeletedAtUtc IS NOT NULL AND Body = '')");
                    table.ForeignKey(
                        name: "FK_TaskComments_WorkspaceMemberships_WorkspaceId_AuthorId",
                        columns: x => new { x.WorkspaceId, x.AuthorId },
                        principalTable: "WorkspaceMemberships",
                        principalColumns: new[] { "WorkspaceId", "UserId" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateTable(
                name: "CommentRevisions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("MySQL:ValueGenerationStrategy", MySQLValueGenerationStrategy.IdentityColumn),
                    WorkspaceId = table.Column<long>(type: "bigint", nullable: false),
                    CommentId = table.Column<long>(type: "bigint", nullable: false),
                    ActorId = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    Action = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommentRevisions", x => x.Id);
                    table.CheckConstraint("CK_CommentRevisions_Action", "Action IN ('Created','Edited','Deleted')");
                    table.ForeignKey(
                        name: "FK_CommentRevisions_AspNetUsers_ActorId",
                        column: x => x.ActorId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CommentRevisions_TaskComments_WorkspaceId_CommentId",
                        columns: x => new { x.WorkspaceId, x.CommentId },
                        principalTable: "TaskComments",
                        principalColumns: new[] { "WorkspaceId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("Relational:Collation", "utf8mb4_0900_ai_ci");

            migrationBuilder.CreateIndex(
                name: "IX_UserNotifications_WorkspaceId_CommentId",
                table: "UserNotifications",
                columns: new[] { "WorkspaceId", "CommentId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_UserNotifications_Kind",
                table: "UserNotifications",
                sql: "(Kind = 'TaskAssigned' AND EventId IS NOT NULL AND CommentId IS NULL AND ScheduleKey IS NULL AND SourceDueDateUtc IS NULL) OR (Kind = 'TaskMention' AND EventId IS NOT NULL AND CommentId IS NOT NULL AND ScheduleKey IS NULL AND SourceDueDateUtc IS NULL) OR (Kind IN ('TaskDue', 'TaskDigest') AND EventId IS NULL AND CommentId IS NULL AND ScheduleKey IS NOT NULL AND SourceDueDateUtc IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_CommentRevisions_ActorId",
                table: "CommentRevisions",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_CommentRevisions_WorkspaceId_CommentId_Version",
                table: "CommentRevisions",
                columns: new[] { "WorkspaceId", "CommentId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskComments_WorkspaceId_AuthorId",
                table: "TaskComments",
                columns: new[] { "WorkspaceId", "AuthorId" });

            migrationBuilder.CreateIndex(
                name: "IX_TaskComments_WorkspaceId_TaskId_Id",
                table: "TaskComments",
                columns: new[] { "WorkspaceId", "TaskId", "Id" });

            migrationBuilder.AddForeignKey(
                name: "FK_UserNotifications_TaskComments_WorkspaceId_CommentId",
                table: "UserNotifications",
                columns: new[] { "WorkspaceId", "CommentId" },
                principalTable: "TaskComments",
                principalColumns: new[] { "WorkspaceId", "Id" },
                onDelete: ReferentialAction.Restrict);
            // Legacy nullable task scope cannot be represented as an EF principal key.
            migrationBuilder.CreateIndex("IX_TaskComments_Scope", "TaskComments", new[] { "WorkspaceId", "ProjectId", "TaskId" });
            migrationBuilder.AddForeignKey("FK_TaskComments_Tasks_Scope", "TaskComments", new[] { "WorkspaceId", "ProjectId", "TaskId" }, "Tasks", principalColumns: new[] { "WorkspaceId", "ProjectId", "Id" }, onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Comment history cannot be downgraded safely. Disable comments and roll forward, or restore a verified backup into a new schema.");
        }
    }
}
