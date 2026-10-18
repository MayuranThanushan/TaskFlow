# TaskFlow frontend

React and TypeScript application built with Vite. See the [root README](../README.md) and [toolchain guide](../docs/toolchain.md) for workspace setup and pinned versions.

From this directory:

```powershell
npm ci
npm run dev
npm run typecheck
npm run build
npm run lint
```

The current page is a minimal application shell. Authentication, navigation, project/task features and API integration are scheduled in the delivery backlog. No sample data or working account actions are implied.

Keep shared startup code in src/main.tsx and the shell in src/App.tsx. Introduce feature folders when implementing actual features. The API proxy and client are not configured yet. Do not add secrets to Vite environment variables: client variables are visible in the browser.
