# Shared UI foundation

SaaS-006, implemented 26 October 2026. This is a structural refactor of the existing UI, with existing project permissions and CRUD behavior retained.

`TaskFlow-FE/src/components/Feedback.tsx` owns reusable field validation messages, request retry feedback, pagination, the keyboard-focusable horizontal table region and inline destructive confirmations. Confirmation focuses the safe cancel action on entry, supports Escape when idle and disables both actions while pending. Feature code retains mutation locks, API calls, error handling, permission decisions and focus restoration; the component cannot grant access or perform a deletion itself. Project deletion, task deletion and member removal use the same confirmation behavior.

Projects, tasks and activity use shared pagination while keeping filters/page state in the URL. Authentication, project and task forms share validation message rendering and retain existing label/error associations. Dashboard, project listing and activity share retry feedback. Task-specific table columns and row actions remain in the task feature.

`TaskFlow-FE/src/hooks/use-api-resource.ts` is the existing resource hook moved out of the project feature and renamed. It preserves account/path identity keys, abort-on-unmount, late-response protection and explicit retry. The activity center now lives under `features/activity/ActivityHub.tsx`; `components/Workspace.tsx` contains presentation-only icons and badges. Loading and access-denied handling remain in each feature where their recovery actions differ.

Validation retains all 95 frontend tests, including failed-write drafts, duplicate submissions, keyboard cancellation, stale responses, account switching, URL history and owner/member controls. The separate 42-scan synthetic UI baseline checks responsive layouts and accessibility. Published-app journeys use actual API mutations and an isolated database; see [verification evidence](evidence/saas006-ui-ci.json). These results do not establish multi-company isolation, which remains planned in SaaS-008 onward.
