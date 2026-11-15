# SaaS-031: server-owned rank and atomic board movement

15 November 2026. Local API/client implementation. Apply migration seventeen, `20261115134543_BoardRank`, before deploying this binary. Existing workspace read/write, CSRF, private-project role and administrator MFA policies remain in force. No new worker, provider, paid resource or board screen is enabled. SaaS-032 supplies the accessible board UI.

## Move contract

`POST /api/v2/workspaces/{workspaceId}/projects/{projectId}/tasks/{taskId}/move` returns the existing TaskDto. The actor comes from the session and scope comes from the route. The required `X-Expected-Version` header is the moved task's captured decimal-string version; a paired CSRF header/cookie is also required. Owner and Editor/legacy Member project writers may move; Viewer access is read-only. Company administration does not override private-project access. Parent/project/task membership, deletion and permission are checked under the existing company -> membership -> project locks.

Example body (replace IDs and revisions with the currently displayed records):

```json
{
  "status": "InProgress",
  "previousTaskId": "101",
  "previousVersion": "4",
  "nextTaskId": "102",
  "nextVersion": "2"
}
```

The previous/next cards are the immediately adjacent cards in the **unfiltered canonical target column**, excluding the moved card. Each supplied ID requires its captured version; null/omitted IDs require null/omitted versions. Both endpoints null means the target column is empty after excluding the moved card. A null previous endpoint means the true start; a null next endpoint means the true end. The first/last card of a filtered or partial page is not necessarily a column boundary. Clients must obtain real boundary/neighbor records rather than invent placement from a loaded page.

Only ToDo/InProgress/Done are accepted. Unknown or duplicate body fields, direct rank, actor/workspace selectors, numeric IDs/versions, unpaired revisions, self-neighbors and duplicate neighbors are rejected. The server chooses the rank. No endpoint moves a task to another project or company.

| Result | Meaning and response |
| --- | --- |
| 200 TaskDto | Move committed, or a current-version request already matches the actual position; returned version is authoritative. |
| 428 resource.version_required | Moved task revision was missing after resource authorization. Reload before submitting a new draft. |
| 400 | Invalid JSON/fields, IDs, status or revision format; no mutation commits. |
| 404 | Company/project/task or neighbor unavailable, foreign, deleted or hidden under current board visibility; no private existence detail is returned. |
| 403 | Known project is read-only for this actor, or existing MFA administration proof is required. |
| 409 resource.conflict | The moved task changed. Keep the user's intent and reload; do not fetch a new revision and silently replay. |
| 409 board.position_conflict | Neighbor revision changed, a card entered the captured gap, or supplied neighbors no longer describe an adjacent position. Reload affected columns and choose a current position. |
| 409 board.capacity_exceeded | Ordering would require a target column beyond the supported 500-card bound. Review project capacity with its owner/operations team. |
| 409 board.rank_exhausted | The ordinary append path reached the integer tail bound. An explicit bounded move can refresh spacing, or operations can review the column. Status/create work rolls back. |

Missing/foreign resources remain denied before task revision checks. Scope admission precedes JSON binding as in existing workspace writes. An authenticated caller cannot obtain another company's validation or revision details by changing route IDs.

## Ordering, concurrency and bounded rebalance

Tasks store a signed BIGINT BoardRank. Rank is internal, never a client-supplied float or an extra editable TaskDto field. Canonical order is BoardRank ascending, then task ID descending for deterministic ties. Rank is per project/status; it is not a meaningful global position across all statuses. Normal creation and the existing status-change endpoint append with spacing 1024. Unchanged status does not reorder a task.

Moves allocate a midpoint between adjacent ranks. If no integer gap remains, the move reassigns spacing in the desired order for **at most 500 visible cards**, including the moved card. It reads at most 501 lightweight target-column metadata rows before refusing oversized work. This matches the current 500-active-task project pilot cap; it does not claim unlimited enterprise columns. The move has a linked 20-second deadline. The rebalance index is `(WorkspaceId, ProjectId, Status, BoardRank, Id)`; rank has no unique constraint, so retained/deleted/import-compatible ties remain deterministic. Zero is retained as the schema import/default value; normal application create/status/move paths allocate positive ranks. Privileged imports need their own reviewed ordering verification.

All ordinary task creation, edit, status, delete, membership and move writers use the same existing company/project gates. The moved task version plus current neighbor versions and adjacency are rechecked after lock acquisition. Two moves of the same card have one version winner; two different cards targeting the same captured gap have one position winner. Moves into unrelated still-valid gaps may proceed sequentially without an unnecessary project-wide revision conflict. A queued writer loses permission if revocation commits before it acquires the gate.

Neighbors use the same live scope/deletion/assignee visibility predicates as board reads. Hidden/ineligible-assignee rows cannot be used as existence-probing anchors. Server ordering never converts a private or cross-company task into a visible board card.

Status, rank, task revision, metadata audit and outbox commit together. The moved task records TaskMoved; rank-only bookkeeping on other affected cards records TaskRankRebalanced and increments their revisions. Both facts contain safe status/rank deltas and scoped identifiers, not task titles/descriptions. The checkpoint worker recognizes the new source kinds. Watchers may receive one TaskMoved update under existing rules; spacing-only rebalance facts do not fan out watcher noise. A current-version no-op changes no rank/timestamp/version and produces no success audit/outbox. Failure in a later audit/outbox save rolls back all earlier rank/version changes.

Always reload the affected summary/columns after success or conflict. The response contains only the moved task, not an unbounded list of rebalance changes. Neighbor edit drafts may now be stale because rebalance increments their versions. [The typed client](../../TaskFlow-FE/src/features/board/board-api.ts) sends captured revisions and CSRF, supports cancellation and propagates conflicts without fetching fresh revisions or automatically replaying the command. Board reads now default to `sortBy=rank&sortDirection=asc`; explicit alternate sorts remain available, but moves always use canonical order. Task-list APIs can explicitly request rank sorting; their created-date defaults stay unchanged.

## Migration and deployment checklist

- [ ] Keep a verified backup and rehearse recovery into a new schema. Use an approved migration identity; never give runtime or workers migration/root credentials.
- [ ] Apply all **seventeen** forward migrations through BoardRank with the existing [repair workflow](../deployment-repair.md). The migration adds the rank column/index and extends the audit-event check constraint for TaskMoved/TaskRankRebalanced.
- [ ] Existing tasks, including retained/legacy rows, receive ranks by prior created-date descending / ID descending within each project/status. The backfill changes neither versions nor timestamps and invents no audit/outbox events. Application writers must be quiesced during the schema upgrade, as for other MySQL DDL releases.
- [ ] Review current [runtime grants](../../deploy/database-roles.sql.example). Existing task INSERT/UPDATE and activity/outbox append privileges already cover rank maintenance; no new privileged worker identity is required.
- [ ] Deploy the matching binary/client contract artifacts and verify exact schema readiness. Old binaries must not serve a newer migration set. The current contract has **72 paths / 94 operations**.
- [ ] Verify populated rank order, first/middle/last/empty-column placement, version/gap conflicts, denied scope, rollback and 500-card rebalance with a disposable company on the selected host. Record query plans, lock wait/deadline behavior and latency there. Local MySQL tests do not certify TiDB or hosted capacity.
- [ ] The Down migration refuses destructive rollback. Disable writes and roll forward, or restore a verified pre-release backup into a new schema with its matching binary. Never delete migration/audit history to force readiness.

See [local release evidence](../evidence/saas031-board-moves.json). Hosted CI/container verification, selected-host migration/locking/load acceptance and the actual board screen remain separate gates. No shared/hosted database or real email activation is performed by this release.

Next: SaaS-032 responsive board UI with keyboard, click/tap and non-drag movement, focus announcements, conflict reconciliation and mobile overflow verification.

SaaS-032 now supplies the [accessible board screen](board-ui.md) and typed confirmation flow. It prepares canonical full-column neighbors before confirmation and reloads affected columns after a committed move; no automatic stale-write replay is added. The dated SaaS-031 API guarantees above remain unchanged.

<!-- current-documentation-index -->
Current implementation, release boundaries and rollout gates: [documentation index](../documentation-index.md). Release-specific counts and follow-ups above describe their recorded release.
