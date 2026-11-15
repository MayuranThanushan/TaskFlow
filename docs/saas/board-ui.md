# SaaS-032: accessible project board UI

15 November 2026. Implemented company-scoped board screen on the SaaS-030 reads and SaaS-031 move API. No new API route, migration, dependency, worker or paid resource is introduced. The current binary still requires all seventeen migrations through BoardRank; API inventory remains 72 paths / 94 operations. Hosted acceptance and manual assistive-technology review remain separate gates.

## Open and read the board

Open Companies -> a project -> Open board, or choose Board view from its task list. The route is `/companies/{workspaceId}/projects/{projectId}/board`. There is no legacy/unscoped board route. The existing workspace frontend configuration controls company routes; the free Render profile already enables them. No separate board flag is added. Deploy matching frontend/backend artifacts with the rank/move contract available.

The board shows To do, In progress and Completed in canonical server rank order, project completion, full column counts, assignee display names and task links. Each column has its own 20-card page controls and explicit shown/total metadata. Loading a later page does not move another column's page. Empty and far-page states are distinct and offer first-page recovery. Counts cover all matching authorized tasks, not only the loaded cards. Separate read responses are checked for count disagreement; a changing board requires an explicit reload instead of displaying contradictory totals.

The board intentionally has no search/priority/assignee filter or alternate sort in this release. Placement must use unfiltered canonical order, and saved views are a later task. Use List view for existing filters. The task list and project page remain available during board/chunk/API failures. The board code and CSS load on demand so other screens do not pay the full board bundle cost.

## Three equivalent movement paths

- Click/tap Move on a card, choose a destination column and position, then Confirm move.
- Focus a card's Move button and use Alt + Up/Down to prepare a one-place reorder or Alt + Left/Right to prepare a move to the adjacent status. Review and confirm in the same dialog. Ordinary Tab/Enter/Space and native select navigation remain available.
- Drag the card's Move handle onto a column (end position) or before another card. Dropping opens the same confirmation dialog; dragging alone never sends a write. The dialog displays the prepared column/position before confirmation.

The native modal has an accessible title/description, explicit focus containment, Escape/Cancel dismissal before saving and focus return to the task. A saving announcement receives focus while controls are disabled. On a rejected write, focus moves to review/reload. After success and refreshed data, focus returns to the moved card, navigating its destination column page as needed. If its Move control is disabled, focus returns to the task link or board heading. Background scrolling is locked while the modal is open. Columns stack on narrow screens and long titles wrap; no drag gesture is required on mobile.

## Captured positions and concurrent changes

When preparing a move, the dialog reads the full target column in at most five 100-card pages, up to the existing 500-card ordering bound. It validates page counts and duplicate/missing cards and excludes the source card from neighbor selection. A later-page first card therefore uses its real previous neighbor, rather than pretending it is the start of the column. Oversized or drifting preparation fails safely and offers explicit recovery. No write is sent during preparation.

Confirmation sends the source task version captured when opening the dialog and the prepared adjacent IDs/versions. It does not fetch a new source revision before submitting. Buttons share a synchronous write guard; a fast double click cannot submit two commands. Company/account/project changes cancel owned reads/writes and unmount the board/draft; late completions cannot populate another scope. No pending move or private task data is stored in local/session storage.

After confirmation, cards/counts/progress update optimistically while saving. An error restores the displayed snapshot and blocks more moves until reload. Conflict review preserves the captured dialog choice and explicitly states that the command was not retried. Reload closes the draft and shows current server data; the user chooses a new position and confirms again. Neither the UI nor client silently retries with fresh revisions. A network failure can have an uncertain outcome, so reload is mandatory even when the displayed change was rolled back.

A successful POST followed by a failed GET is handled separately: the UI says the move was saved but the view could not refresh. It does not claim the committed transaction was rolled back. Scope/access failures clear private cards/drafts rather than leaving an editable stale board. Viewer controls remain read-only; server authorization, CSRF and any administrator MFA proof remain authoritative. The board never grants private-project access based on a company role.

## Verification and release checklist

- [ ] Apply all seventeen migrations and deploy matching artifacts. SaaS-032 itself adds no DDL or grants; see [rank/move rollout](board-moves.md).
- [ ] Verify owner/editor and viewer behavior in a disposable company on the selected host.
- [ ] Verify real first/middle/last/later-page placement, current counts/progress, conflicts, failed-save rollback and failed post-commit refresh.
- [ ] Verify keyboard-only and touch/click alternatives, screen-reader announcements and focus return with the assistive technologies supported for customers.
- [ ] Verify representative project size and selected-host transaction/query latency. Local mocked-browser results do not certify hosted API authorization, capacity or commercial readiness.

The frontend suite includes board component/client/state cases; backend board/move/contract tests run against an isolated MySQL schema. The [real-browser UI review](../../tests/browser/board-ui.cjs) exercises the production frontend with **all API requests mocked**, using only a loopback origin. It covers native focus/Escape, actual pointer drag, keyboard movement, conflict review, viewer access, independent paging and desktop/320px accessibility scans. It never uses real credentials or a shared database. See [release evidence](../evidence/saas032-board-ui.json).

To reproduce the mocked browser review, build a workspace-enabled frontend, run its preview in one terminal and run the review in another:

```powershell
Set-Location D:\TaskFlow
$env:VITE_WORKSPACES_ENABLED = 'true'
$env:VITE_WORKSPACE_LIFECYCLE_ENABLED = 'true'
npm run build --prefix TaskFlow-FE
npm run preview --prefix TaskFlow-FE -- --host 127.0.0.1 --port 5397 --strictPort
```

```powershell
Set-Location D:\TaskFlow
$env:TASKFLOW_BOARD_UI_ORIGIN = 'http://127.0.0.1:5397'
$env:TASKFLOW_BOARD_UI_MOCKS = '1'
node tests/browser/board-ui.cjs
```

Install the locked browser review tooling first as described in [browser setup](../../tests/browser/README.md). On Windows the review defaults to Edge; `TASKFLOW_BROWSER_EXECUTABLE` selects an already installed compatible browser. Output goes to TestResults/saas032-browser. Loopback HTTP is only for this fully mocked UI review; it does not change application authentication HTTPS policy.

Next: SaaS-033 tenant-scoped My work, due/overdue queues and authorized search.

<!-- current-documentation-index -->
Current implementation, release boundaries and rollout gates: [documentation index](../documentation-index.md). Release-specific counts and follow-ups above describe their recorded release.
