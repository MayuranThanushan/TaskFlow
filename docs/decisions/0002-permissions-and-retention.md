# 0002: Permissions, ownership and retention

Status: adopted. Date: 18 October 2026.

## Context

The SRS leaves member mutation rights configurable; the ERD suggests Owner/Admin/Member without requiring all three. Ownership can otherwise be duplicated between Projects and ProjectMembers. Deletion and activity retention are unresolved.

## Decision

Use Owner and Member only. Projects.OwnerId is the authoritative owner. Persist only non-owner memberships, with role Member. Synthesize the owner's entry in membership responses using project creation time as joinedAtUtc. An owner is always an eligible assignee. Defer Admin, role changes, ownership transfer and self-service leave.

| Operation within a project | Owner | Member | Outsider |
| --- | --- | --- | --- |
| Read project/tasks/members/activity | Yes | Yes | No |
| Update project | Yes | No | No |
| Delete project | Yes | No | No |
| Add/remove members | Yes | No | No |
| Create/edit/assign tasks and change status | Yes | Yes | No |
| Delete task | Yes | No | No |

Every authenticated user can create projects and edit their own display name. Membership creation accepts an existing user ID and role Member; no directory search or email invitation is added. Duplicate membership/adding owner returns 409. Removing owner returns 409. An unsupported role returns 400. Removing a nonexistent membership returns 404.

Check resource access using current database state on every request, not role claims cached at sign-in. Inaccessible/missing/deleted resource returns 404; a visible resource's forbidden operation returns 403. Unauthenticated requests return 401 before resource lookup. Direct task IDs must be resolved through their accessible active project.

Add nullable DeletedAtUtc to Project and TaskItem. DELETE means soft delete. Exclude deleted projects and their tasks from all ordinary queries, aggregates, membership operations, assignments and activity endpoints. Exclude individually deleted tasks from task reads/counts while retaining their history in active-project activity. Preserve stored memberships and history. Defer restore/purge/account deletion. Use restrictive foreign-key delete behavior.

Mutations and their activity records commit in one transaction. Removing a member unassigns that user's active tasks and records MemberRemoved and TaskUnassigned events atomically. Existing soft-deleted task records retain historical assignments. Serialize project-scoped mutations by locking the parent project row for the transaction, consistently acquiring the project lock before membership/task locks. Recheck active-project status, caller access and assignee eligibility inside that boundary. This covers assignment/removal and deletion/write races. Keep transactions short; normal requests that overlap logout/removal may finish if already authorized before the change commits.

Activity has a defined action type and structured backend-created details. No public write/update/delete activity endpoint exists. Include ProjectCreated, ProjectUpdated, ProjectDeleted, MemberAdded, MemberRemoved, TaskCreated, TaskUpdated, TaskAssigned, TaskUnassigned, TaskStatusChanged, TaskDeleted. A no-op mutation does not create activity or change UpdatedAtUtc. Deleted projects' history is retained internally without adding a public deleted-project history UI.

## Consequences and verification

Soft deletion adds schema/query rules but preserves history. All visibility tests must include deleted parent projects. Test owner protection, cross-user IDs, member restrictions, removal taking effect, transaction rollback, concurrent removal/assignment, and deletion visibility. The project's row lock is a concrete MySQL implementation requirement for the later persistence task.
