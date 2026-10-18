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

The current page is a minimal application shell. Authentication, navigation, project/task features and API integration are scheduled in the delivery backlog. No sample data or working account actions are implied.

Keep shared startup code in src/main.tsx and the shell in src/App.tsx. Introduce feature folders when implementing actual features. The development /api proxy is configured; the application HTTP client arrives with authentication. See [API foundation](../docs/api-foundation.md) for proxy configuration. Do not add secrets to Vite environment variables: client variables are visible in the browser.

Vitest uses jsdom and Testing Library; use `npm run test:watch` for interactive work or `npm run test:ci` for JUnit output. See [testing and CI](../docs/testing.md).
