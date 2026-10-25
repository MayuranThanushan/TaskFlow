# Published-app browser review

Run from the repository root against an isolated, disposable Production-mode HTTPS app and MySQL database. These journeys create accounts/projects/tasks, change names, memberships and statuses, and soft-delete fixtures. Provisioning and final database deletion belong to the caller; never point this suite at shared or production data.

Use the repository Node version and install the locked test-only dependencies:

```powershell
npm ci --prefix tests/browser
```

The suite uses installed Edge by default. Set `TASKFLOW_BROWSER_EXECUTABLE` to an installed Chromium/Edge executable when needed. On Linux a Playwright-managed Chromium can be installed with `npx --prefix tests/browser playwright-core install --with-deps chromium`; set the executable to `chromium.executablePath()` from that package. No browser or test dependency is shipped in the app.

Prepare a fresh owner account whose initial display name is `Deployment smoke`. The existing `scripts/smoke_deployment.py --register` flow creates this owner, verifies authentication and signs out. Supply its credentials through `TASKFLOW_SMOKE_EMAIL/PASSWORD`, then reuse them as `PROJECT_OWNER_EMAIL/PASSWORD`. Choose a different unused `BROWSER_TEST_EMAIL` for the registration journey. Keep these synthetic accounts inside the disposable database.

Before running, set Projects.AUTO_INCREMENT to 9007199254741111 and Tasks.AUTO_INCREMENT to 9007199254741999 using the database's restricted provisioning connection. The suite deliberately requires generated project/task IDs above JavaScript's safe integer limit. The owner must have no projects before the suite. This setup must only modify the disposable schema.

```powershell
$env:TASKFLOW_BROWSER_ORIGIN = 'https://localhost:15173'
$env:TASKFLOW_BROWSER_DISPOSABLE = '1'
$env:TASKFLOW_BROWSER_CA_FILE = '/path/to/public-localhost.pem'
$env:PROJECT_OWNER_EMAIL = 'fresh-owner@example.test'
$env:PROJECT_OWNER_PASSWORD = 'Synthetic-Deploy-Pass123!'
$env:BROWSER_TEST_EMAIL = 'fresh-browser@example.test'
node tests/browser/run.cjs
```

For a publicly trusted certificate omit TASKFLOW_BROWSER_CA_FILE. A supplied private certificate is pinned by its public key in the test browser; global certificate-validation bypasses are not enabled. Protocol smoke checks validate the supplied CA normally. Secrets/PFX files are not browser inputs.

The runner executes authentication, project/task/membership/dashboard/activity journeys, profile editing, then the accessibility/keyboard/security/performance review. A real 429 on sign-in waits for Retry-After before one explicit resubmission; application rate limits remain unchanged. API probes use the same browser connection/cookies. The accessibility engine is served through a browser-only same-origin intercepted asset, preserving the deployed CSP.

Results and screenshots go to ignored TestResults/browser, or TASKFLOW_BROWSER_OUTPUT. review.json includes the browser version, scan findings and incomplete checks, keyboard/security results, fixture totals and operation timings. There are 20 timed samples after three warmups for each of 11 operations at concurrency one; TASKFLOW_BROWSER_SAMPLES accepts 5-100. Each warm maximum must remain below 2,000 ms. This measures demonstration behavior, not production load capacity. The final review project is soft-deleted; caller cleanup must remove the entire owned disposable database and stop verification services. No cookies, credentials or private key artifacts are written by this suite.

See [step 21 evidence](../../docs/verification-review.md). The CI deployment job now installs Chromium and runs this suite against its disposable Production HTTPS container after restart/session verification. Hosted execution evidence and selected-host release checks remain separate gates; see the [CI gate policy](../../docs/ci-release-gates.md).

## Local workspace UI review

With the frontend running on http://localhost:5173, run from the repository root:

```powershell
npm ci --prefix tests/browser
node tests/browser/workspace.cjs
```

This separate review intercepts every /api/v1 request with synthetic in-memory fixtures. It does not create real accounts or modify the development database. It accepts only localhost/127.0.0.1 origins (optional TASKFLOW_UI_ORIGIN), uses installed Edge, and generates screenshots plus TestResults/workspace-ui/review.json. It covers 42 route/viewport scans at 320/390/768/1440, axe WCAG A/AA checks, page overflow, owner/member controls, task/project CRUD UI, inline status, keyboard confirmation cancellation/table scrolling, profile, password visibility, form focus and current-page audit JSON download. This validates UI interactions, not backend authorization, database transactions, production CSP or deployed-host behavior; use the published-app suite above for those.
