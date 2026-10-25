# Workspace UI and UX refresh

25 October 2026. The existing React application now shares a restrained indigo/slate design system, consistent controls and readable focus states. This is a local UI delivery; existing container/hosted release gates remain in the [handoff](handoff.md).

## Screens and behavior

- The authenticated shell has a persistent desktop sidebar, active navigation, workspace context, account identity and sign-out. Mobile navigation wraps without hiding the core routes. Route changes keep main-content focus and document titles.
- Overview shows real scoped totals, task distribution, completion percentage and overdue attention. Up to four recently updated projects are loaded independently. Selecting Preview tasks loads that project's latest five tasks and preserves a direct link to the complete task list. Empty charts remain neutral; absent/loading data is never presented as invented metrics.
- Project cards show Owner/Member, description, updated date, and direct Tasks/Activity links. Edit links appear for the owner. Search, sorting, paging and browser history retain their server contracts. Project detail places owner actions beside the title and groups brief, tasks, history, membership and progress into a responsive grid.
- Tasks use a semantic table with priority badges, member display names, deadlines and edit/open actions. Status changes can be made inline. Pending writes disable the row control; errors retain the previous value and expose retry through another selection. Success reloads the scoped list/count so filters remain accurate. The table is independently scrollable and keyboard-focusable on narrow screens; overdue indicators refresh each minute while the list is mounted.
- The Activity center links only authorized projects to their own timelines. It lists at most 100 projects and directs users to project search for additional histories. Timelines show human-readable action labels, current member names where available, authoritative actor/task/event IDs and local timestamps, with retained summaries. Removed actors still have ID attribution. Date filters/pagination/refresh remain server-backed.
- Export this page downloads JSON containing only the current filtered page, project ID, filter values, page, total count, original event records and UTC export time. IDs stay strings. It does not imply an export of all history and adds no history mutation endpoint.
- Sign-in and registration share the refreshed visual language and support Show/Hide password without submitting. The illustrative welcome card is decorative. Validation retains field association, first-invalid-field focus and password policy. No unsupported password-recovery flow is advertised.
- Project/task create and edit forms use consistent spacing and contextual guidance; task details group fields at desktop widths and stack on mobile. Profile separates read-only identity information from editable display name. Confirmed deletion, cancellation/Escape, loading/error recovery, duplicate-write prevention and owner/member rules are retained.

## Implementation boundaries

Shared icons/badges and the activity entry screen live in [Workspace.tsx](../TaskFlow-FE/src/components/Workspace.tsx). [workspace.css](../TaskFlow-FE/src/workspace.css) supplies tokens, component treatments, responsive layouts and contrast corrections over the existing base styles. The new status control calls the existing CSRF-protected task-status API; its server remains authoritative. No dependencies, database schema, API permission rules, credentials or real user records were changed.

Actor names are current accessible member names, not immutable historical identity snapshots; IDs remain authoritative. Activity descriptions remain stored audit summaries. The project list/dashboard use bounded requests rather than fetching per-project details for every card. The activity export is JSON to preserve opaque IDs and avoid spreadsheet formula interpretation.

## Verification and evidence

Frontend type-check, lint, all 95 Vitest tests and production build pass. Added regressions cover recent-project task switching, failed/pending inline status changes with filtered-list reload, and password visibility/input retention. Existing permission, identity-change, stale-request, validation, error recovery and confirmed deletion checks continue passing. Historical backend verification is 122 tests; this UI change does not claim a new live backend suite run.

The [browser review](../tests/browser/workspace.cjs) passed 42 axe WCAG A/AA route/viewport scans, with zero detected violations, zero page overflow and zero browser script errors. Viewports cover 320, 390, 768 and 1440 pixels. Screens include overview, projects, project detail, task list/detail, project/task create/edit, activity hub/feed, profile, home, not-found, sign-in and registration. Keyboard review covered confirmation focus/Escape restoration, form validation focus and scrolling the task table. Owner/member control visibility, project/task CRUD, inline status, profile editing, password visibility and JSON download passed.

Browser requests were intercepted with synthetic API fixtures; no real user accounts or database data were modified. Those checks establish UI behavior, not production security certification or an independent audit. Backend policies continue to be covered by their existing integration tests. Deployment/browser compatibility outside the recorded local Edge environment remains a release check.

[Sanitized machine-readable evidence](evidence/workspace-ui.json) is committed. Screenshots and the full browser report are generated under ignored TestResults/workspace-ui, including overview-desktop.png, tasks-desktop.png, activity-desktop.png, login-1440.png and mobile/form variants. Reproduce them with the [browser instructions](../tests/browser/README.md#local-workspace-ui-review).
