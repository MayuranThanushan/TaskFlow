# Dashboard totals and project progress - step 18

The overview now shows live scoped project/task totals, assigned-to-me and overdue counts, and status totals. Existing project detail progress is verified against the same active-record rules. No schema or dependency change is required.

## API and aggregate rules

GET `/api/v1/dashboard` requires authentication and returns `{totalProjects,totalTasks,tasksByStatus:{toDo,inProgress,done},assignedToMe,overdueTasks}` with no-store caching. Query parameters are unsupported and return safe validation 400. Anonymous requests return JSON 401.

Project scope uses the shared current owner/member query. Task scope includes only active tasks whose active parent is visible. EXISTS-based authorization avoids multiplying rows through membership joins, including redundant owner/member data. Assignment alone never grants access. Assigned-to-me includes any active scoped task assigned to the current user, including completed tasks. Status totals partition totalTasks.

Overdue means a non-null UTC deadline strictly before the captured current UTC instant and status other than Done. Equality is not overdue; future/null deadlines and completed tasks are excluded. Project/task soft deletion and membership removal apply before aggregation.

The service captures one TimeProvider instant, then reads project count and a grouped task aggregate in a single REPEATABLE READ transaction. All totals therefore use a consistent scope and snapshot even if another request creates a project/task between statements. An empty task group produces explicit zero values. Counts are nonnegative.

Project progress remains on GET `/api/v1/projects/{projectId}`: active totalTasks/completedTasks and integer progressPercentage, rounded midpoint away from zero. Empty projects show 0%; complete projects show 100%. Individually deleted completed tasks do not inflate progress, and deleted/inaccessible parents return 404.

## Frontend and refresh

The protected overview displays labelled workspace/status counts, keeps the signed-in account ID and provides project navigation. It handles loading without fabricated zero values, safe failure/retry, an empty workspace and projects with no tasks. Refresh overview explicitly reloads current counts; navigating back after task/project/member mutations fetches fresh data. Project detail likewise reloads when revisited from task mutations. The UI does not poll; cross-tab changes can be loaded with Refresh overview.

The shared identity-scoped resource loader cancels/ignores superseded reads. Account changes remount the overview, clear old counts and ignore previous-account responses. Dashboard 401 responses use the existing expiry redirect. The layout uses responsive definition lists rather than client-side recomputation or additional aggregate endpoints.

## Verification

All 100 backend tests passed with real MySQL and zero skips (34 non-database and 66 live checks). Five new HTTP/database cases verify authentication/no-store/zero results, owner/member overlap without duplicate counts, private and deleted scope, null/equal/past/future deadlines at microsecond precision, completed overdue exclusion, mutation-driven counts and assignment/removal, project progress at zero/partial/midpoint/full values, and a forced concurrent creation plus clock advance between aggregate statements. That last test proves both snapshot consistency and one captured time.

All 78 frontend tests passed, including six new overview cases for protection, real totals, loading/empty states, safe failure/retry/refresh/navigation, expiry and late previous-account responses. Existing auth tests use an explicit zero dashboard fixture. Type checking, ESLint, Release and production builds passed; no migration is pending. Existing CI discovers the tests. Hosted CI, Docker execution and selected-host verification remain pending.

Step 19 [activity reading](activity.md) is implemented. Step 20 [profile editing](profile.md) is implemented; step 21 [verification review](verification-review.md) is complete; step 22 [release/recovery preparation](release-runbook.md) is complete locally; next is step 23.

The published Production release passed verified HTTPS migration/readiness/restart smoke checks and real Edge zero/populated totals, owner/member scope, overdue/completion, cross-tab explicit refresh, assignment/removal, task/project deletion and 0/100 project progress. Desktop/mobile screenshots were captured and the mobile layout inspected without horizontal overflow. Existing project/task/account/removal/query checks also passed. The disposable database was removed and verification services stopped.
