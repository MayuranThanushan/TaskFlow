using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TaskFlow.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WorkspaceReferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Compatibility-stage SQL constraints: nullable columns cannot be EF alternate keys
            // without EF making them required. Manage these explicitly until the NOT NULL cutover.
            migrationBuilder.CreateIndex("IX_Projects_WorkspaceId_OwnerId", "Projects", new[] { "WorkspaceId", "OwnerId" });
            migrationBuilder.CreateIndex("IX_ProjectMembers_WorkspaceId_ProjectId", "ProjectMembers", new[] { "WorkspaceId", "ProjectId" });
            migrationBuilder.CreateIndex("IX_ActivityLogs_WorkspaceId_ProjectId_TaskId", "ActivityLogs", new[] { "WorkspaceId", "ProjectId", "TaskId" });
            migrationBuilder.AddForeignKey("FK_Projects_WorkspaceMembers_Owner", "Projects", new[] { "WorkspaceId", "OwnerId" },
                "WorkspaceMemberships", principalColumns: new[] { "WorkspaceId", "UserId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_ProjectMembers_Projects_Workspace", "ProjectMembers", new[] { "WorkspaceId", "ProjectId" },
                "Projects", principalColumns: new[] { "WorkspaceId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_ProjectMembers_WorkspaceMembers_User", "ProjectMembers", new[] { "WorkspaceId", "UserId" },
                "WorkspaceMemberships", principalColumns: new[] { "WorkspaceId", "UserId" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_Tasks_Projects_Workspace", "Tasks", new[] { "WorkspaceId", "ProjectId" },
                "Projects", principalColumns: new[] { "WorkspaceId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_ActivityLogs_Projects_Workspace", "ActivityLogs", new[] { "WorkspaceId", "ProjectId" },
                "Projects", principalColumns: new[] { "WorkspaceId", "Id" }, onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey("FK_ActivityLogs_Tasks_WorkspaceProject", "ActivityLogs", new[] { "WorkspaceId", "ProjectId", "TaskId" },
                "Tasks", principalColumns: new[] { "WorkspaceId", "ProjectId", "Id" }, onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey("FK_ActivityLogs_Tasks_WorkspaceProject", "ActivityLogs");
            migrationBuilder.DropForeignKey("FK_ActivityLogs_Projects_Workspace", "ActivityLogs");
            migrationBuilder.DropForeignKey("FK_Tasks_Projects_Workspace", "Tasks");
            migrationBuilder.DropForeignKey("FK_ProjectMembers_WorkspaceMembers_User", "ProjectMembers");
            migrationBuilder.DropForeignKey("FK_ProjectMembers_Projects_Workspace", "ProjectMembers");
            migrationBuilder.DropForeignKey("FK_Projects_WorkspaceMembers_Owner", "Projects");
            migrationBuilder.DropIndex("IX_ActivityLogs_WorkspaceId_ProjectId_TaskId", "ActivityLogs");
            migrationBuilder.DropIndex("IX_ProjectMembers_WorkspaceId_ProjectId", "ProjectMembers");
            migrationBuilder.DropIndex("IX_Projects_WorkspaceId_OwnerId", "Projects");
        }
    }
}
