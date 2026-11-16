# SaaS-033 ? My work and authorized search

Open a company and choose **My work** in the sidebar. The default shows unfinished tasks assigned to your account across projects you can currently access. Choose **Unassigned** explicitly for unclaimed work, or **All visible work** to broaden assignment. Company administration alone does not grant access to private projects.

## Queues and filters

Open work includes unfinished tasks with no deadline. Due soon includes unfinished deadlines from the server cutoff through seven days later, both inclusive. Overdue means strictly before the cutoff; completed tasks are excluded from both deadline queues. All tasks includes completed work.

Search matches task titles or visible project names, with a trimmed maximum of 200 characters. It does not search descriptions. Assignment, project, status, priority and date filters combine with AND. Queue counts apply all filters except the selected queue. Dates entered in the screen include the whole local calendar day, including the last database microsecond; share links preserve the calendar filters. Explicit UTC timestamp links remain supported.

Task, project and board links open existing authorized detail/action screens. This summary screen performs no writes. Changes in access or invalid filters display recovery controls without keeping old private rows on screen.

## API contract

`GET /api/v2/workspaces/{workspaceId}/work` uses the existing default-off workspace read gate and active company admission before query validation. Defaults: `assignment=mine`, `queue=open`, `page=1`, `pageSize=20`, `sortBy=dueDateUtc`, `sortDirection=asc`.

Parameters: `assignment` mine/unassigned/all; `queue` open/due/overdue/all; optional `search`, `projectId`, canonical `status` and `priority`, explicit-offset `fromUtc`/`toUtc`; sort by dueDateUtc/createdAtUtc/title, asc/desc. Pages contain at most 100 rows. Unknown/repeated parameters, invalid values and inverted ranges return 400. An inaccessible project filter returns 404. A far page stays empty while preserving truthful totals.

Inclusive lower bounds round up and upper bounds round down to MySQL microseconds. Deadline sorting places null deadlines last in either direction; descending task ID breaks ties. Counts and page rows use one repeatable-read snapshot and one captured server cutoff. Compact rows contain task identity/status/priority/dates/version plus project and assignee names; descriptions are excluded. Query count does not grow with project or row count.

## Rollout and limitations

No migration or worker is added. Keep all seventeen existing migrations through BoardRank. The contract is now 73 paths / 95 operations. Existing preview flags apply; this release does not activate hosted features or email. Search uses relational substring matching, not a full-text index; selected-host latency/capacity remains a release gate. Paging is stable within each response snapshot, not across separate requests after concurrent edits.

Saved personal/shared views and persisted list/board/filter preferences remain SaaS-034. Human assistive-technology review and selected-host verification remain separate from local automated tests.

[Current documentation index](../documentation-index.md)
