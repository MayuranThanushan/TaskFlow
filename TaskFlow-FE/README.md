# TaskFlow frontend

React and TypeScript application built with Vite. See the [root README](../README.md) and [toolchain guide](../docs/toolchain.md) for workspace setup and pinned versions.

From this directory:

```powershell
npm ci
npm run dev
npm run typecheck
npm test
npm run build
npm run lint
```

The application includes responsive registration/sign-in screens, cookie session restoration, protected workspace navigation and logout. Project creation, authorized listing/detail, search/sort/pagination and responsive project pages are implemented; see [projects](../docs/projects.md). Owner-only editing and confirmed soft deletion are implemented. Member listing and owner-managed add-by-ID/confirmed removal are implemented; the overview shows your account ID. Task creation, paginated/searchable listing and direct detail are implemented; see [tasks](../docs/tasks.md). Complete task filter/sort controls preserve URL history; see [task queries](../docs/task-queries.md). Task editing/status changes and confirmed owner-only deletion are implemented; see [task mutations](../docs/task-mutations.md). See [frontend authentication](../docs/frontend-authentication.md) for behavior and verification.

Keep startup code in src/main.tsx, the shell/routes in src/App.tsx, the shared client in src/api and authentication in src/features/auth, project pages/data loading in src/features/projects, and task screens/types in src/features/tasks. The development /api proxy and centralized Axios client are configured. Account flows require the [HTTPS setup](../docs/authentication.md#local-https-setup). See [API foundation](../docs/api-foundation.md) for proxy configuration. Do not add secrets to Vite environment variables: client variables are visible in the browser.

Vitest uses jsdom and Testing Library; use `npm run test:watch` for interactive work or `npm run test:ci` for JUnit output. See [testing and CI](../docs/testing.md).
