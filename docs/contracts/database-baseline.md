# Database contract amendments: implementation baseline

Date: 18 October 2026. Applies to `Documents/ERD - TaskFlow.docx`. Implements decisions [0002](../decisions/0002-permissions-and-retention.md) and [0003](../decisions/0003-data-and-api-semantics.md). No migrations or database connection are created in step 01.

## Mapping and constraints

Use PascalCase application table/column names consistently. ApplicationUser derives from IdentityUser<long>; use Identity's user table (AspNetUsers) as the only user store, including DisplayName VARCHAR(100), CreatedAtUtc and UpdatedAtUtc DATETIME(6). Identity controls authentication columns and ancillary tables. Do not create a second Users table.

All keys/FKs are signed BIGINT. Configure normalized email uniqueness explicitly with a unique index on NormalizedEmail, require a valid non-null email through account operations, and enable Identity's unique-email policy. Use a documented utf8mb4 case-insensitive MySQL collation consistently for user normalization/search and project/task text. Review provider-generated key lengths before applying migrations.

| Table | Agreed fields / amendments |
| --- | --- |
| Projects | Id, OwnerId, Name VARCHAR(150), nullable Description TEXT, CreatedAtUtc, UpdatedAtUtc, **nullable DeletedAtUtc DATETIME(6)** |
| ProjectMembers | Composite PK (ProjectId, UserId), Role VARCHAR(30) fixed to Member, JoinedAtUtc; **owner has no row** |
| Tasks | Id, ProjectId, nullable AssigneeId, Title VARCHAR(200), nullable Description TEXT, Status VARCHAR(30), Priority VARCHAR(20), nullable DueDateUtc, CreatedAtUtc, UpdatedAtUtc, **nullable DeletedAtUtc DATETIME(6)** |
| ActivityLogs | Id, ProjectId, ActorUserId, nullable TaskId, ActionType VARCHAR(50), nullable Details TEXT, CreatedAtUtc |

All timestamps are DATETIME(6), interpreted as UTC. Descriptions are bounded to 10,000 UTF-16 code units by application validation. Required names/titles cannot be blank. Store status/priority enum names explicitly; add database check constraints for ToDo/InProgress/Done and Low/Medium/High where supported by the selected MySQL/provider combination. Add a Member-only role constraint. Identity and application key types must match exactly.

Project owner and member FKs reference AspNetUsers. Tasks and activity reference Projects. Task assignee and activity actor reference AspNetUsers. Activity.TaskId references Tasks. Configure every relationship's hard-delete behavior explicitly as Restrict/NoAction, verified on the selected provider. Do not cascade-delete history. Account deletion and physical purge are not exposed in the MVP.

## Visibility and transactions

An active project has DeletedAtUtc IS NULL. A visible task must have both an active project and its own DeletedAtUtc IS NULL. Scope every list, direct detail, dashboard, progress, assignment and membership operation accordingly. Project activity requires an active accessible project, but may contain events for individually deleted tasks. No public DTO exposes soft-delete columns.

Project ownership comes only from Projects.OwnerId; synthesize the owner in membership responses. Prevent owner membership and assignment to nonmembers in application operations. Database foreign keys alone cannot enforce these cross-table rules.

For project-scoped writes acquire the project-row lock first, recheck current access/deletion, then modify membership/tasks and append relevant activity within the same transaction. Lock ordering is project first, then affected records in stable key order. Removing a member clears their active task assignments before committing. Roll back all changes if either data or activity persistence fails. Soft-delete updates DeletedAtUtc and UpdatedAtUtc in the same mutation transaction.

## Initial indexes

- AspNetUsers: unique NormalizedEmail in addition to Identity's required indexes.
- Projects: OwnerId.
- ProjectMembers: PK (ProjectId, UserId), reverse index (UserId, ProjectId).
- Tasks: (ProjectId, Status), (ProjectId, DueDateUtc), AssigneeId.
- ActivityLogs: (ProjectId, CreatedAtUtc, Id), TaskId.

Inspect generated SQL/indexes for duplicates and validate actual query plans before adding more. Do not store dashboard counters or project progress. Read dependent counts under a consistent statement/snapshot.

## Migration acceptance gate

Use the versions selected in [decision 0004](../decisions/0004-runtime-and-database-provider.md): Oracle MySQL provider 10.0.9, EF Core/Identity 10.0.11, MySQL target 8.4.11. Then generate and inspect the initial migration, apply it to an isolated clean MySQL database, and inspect actual constraints/indexes. Test duplicate normalized emails and memberships, FK rejection, enum rejection, soft-deleted parent visibility, transaction rollback, and concurrent member removal/assignment. Provider selection is complete in step 02; entities/mappings and the initial migration were implemented and live-verified in step 05. See [database setup](../database-setup.md) for evidence and remaining feature-level checks.
