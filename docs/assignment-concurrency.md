# Assignment cleanup and concurrent member removal - step 16

The assignment/removal implementation from steps 13-15 satisfies the shared parent-lock policy. Step 16 completes deterministic HTTP/database concurrency coverage and stale-form verification; no production code, schema or dependency change was necessary.

## Verified behavior

Every task creation/edit/status write and membership mutation acquires the parent project row lock first in a fresh READ COMMITTED transaction. Access, active project/task state and assignee eligibility are rechecked after acquiring the lock. Direct task routes may resolve a parent before waiting; that lookup never replaces the check inside the transaction.

- An assignment that holds the lock first may commit. Queued removal then clears the newly committed assignment before it returns 204.
- Removal holding the lock first commits before a queued owner assignment. The assignment returns field-level 400 on assigneeId, with no partial title/content changes or activity.
- Removed-member creation/edit/status requests queued behind removal return safe 404. Their login session remains valid; project access was revoked independently of authentication.
- A member write already authorized under the lock may finish before queued removal. Removal then clears all active assignments, including newly created tasks. Later writes by that account return 404.
- Removal unassigns all active task statuses, including Done, and updates those tasks' audit timestamps. It preserves individually deleted task assignments/timestamps, other assignees and tasks/membership in other projects.
- Re-adding an account restores project access without restoring previous task assignments. A later explicit eligible assignment is permitted.
- Failure inserting MemberRemoved activity rolls back membership removal, every task unassignment and all related history. The transaction releases its lock; a queued owner assignment then sees the restored membership and succeeds.

Each cleared task receives TaskUnassigned with its task ID and backend-created userId detail; removal receives MemberRemoved. Events and domain changes commit atomically. There are no write retries or callback replays. Already-authorized overlapping requests may complete, as adopted in decision 0002.

## Deterministic test method

Eight new live test cases exercise the behavior through actual cookie/CSRF-protected HTTP endpoints against MySQL. A test-only DbCommandInterceptor pauses the first request after MySQL executes its parent SELECT FOR UPDATE. The competing request must reach that lock attempt and remain incomplete before the gate is released. Both request orderings are forced rather than left to scheduler timing. Gates and waits are bounded; finally releases the held transaction on assertion failures. One case covers create/edit/status before removal; separate cases cover each queued removed-member write.

Another case injects a real foreign-key failure specifically on MemberRemoved history while an owner assignment waits behind it. Cleanup assertions verify persisted membership, all affected rows and event history after rollback. Existing random-overlap, activity-failure and deletion-race tests remain in the suite. The shared HTTP host accepts multiple test interceptors; application service registrations are unchanged.

## Stale frontend drafts

Two new frontend cases verify that an owner title edit opened before removal omits its unchanged assignee and therefore cannot reapply the cleared assignment. A stale selected assignee rejected by the server preserves the draft and field error; the owner can explicitly retry unassigned without an automatic duplicate write. Untouched fields remain omitted by the presence-aware edit form.

## Verification

Release build passed without warnings/errors. All 90 backend tests passed with real MySQL and zero skips (34 non-database and 56 live checks). All 66 frontend tests, type checking, ESLint and production build passed. Production frontend asset hashes are unchanged from step 15. No migration is pending. Existing CI discovers the new cases; hosted CI, Docker execution and selected-host verification remain pending.

Step 17 [task filtering/sorting/pagination](task-queries.md) is now complete. Next is step 18 dashboard totals and project progress.

The unchanged step 15 Production artifact passed a fresh HTTPS migration/readiness/restart rehearsal and real Edge multi-tab verification: a stale owner edit kept a removal-cleared assignment null, a stale selected assignee returned a field error while retaining the draft, explicit unassigned retry succeeded, and removed-member create/edit/status each returned 404. Existing account/project/task/mobile checks also passed. The disposable database was removed and verification services stopped.
