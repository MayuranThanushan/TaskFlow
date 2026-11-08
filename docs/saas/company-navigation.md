# Company creation and navigation

SaaS-011, 28 October 2026. Company creation, a paginated company directory, explicit company URLs and a scoped API client are implemented as an opt-in preview. Default builds keep the UI disabled and both backend preview flags remain false. This does not complete the customer company cutover.

## Company creation

`POST /api/v2/workspaces` accepts only `{ "name": "Company name" }`. It requires a validated session, paired CSRF cookie/header and both backend preview flags. Names are trimmed and limited to 150 characters; unknown/duplicate fields and all query parameters are rejected. There is no client-selected owner, role, workspace ID or legacy mapping.

The company and the caller's active Owner membership commit in one transaction. A failed membership save rolls back the company. The caller does not need membership in another company to create one. Explicit companies have no LegacyOwnerId; the offline legacy backfill must not be used to regroup them. Duplicate names are allowed; the ID identifies the company. Creation is not automatically retried after a network/server failure. Creation quotas, invitation/approval policies and company-level audit events remain later release work; project activity does not pretend to be a company administration audit trail.

`GET /api/v2/workspaces/{workspaceId}` returns the active caller's company ID, name and role. It requires the read flag, rejects queries and returns 404 for missing/suspended membership. The existing directory remains paginated and excludes inactive affiliations. Company administration still grants no private-project override.

## Navigation and request lifetime

Open `/companies` to create or choose a company. Resource URLs carry `/companies/{workspaceId}/…`, including project, task and activity deep links. The browser's URL is the company context; there is no mutable global current-company preference, cookie or local-storage key. Reload, return-after-login and browser history keep the explicit scope. Profile and authentication remain global account routes. Legacy URLs remain available during this preview.

The directory is keyed by authenticated identity. Company content is keyed by identity and company. Switching unmounts forms, results and filter state; each company's overview opens with fresh filters. The switching UI states that open forms are cleared. Scoped links and successful-save navigation retain the originating company. Company metadata must load successfully before private resource pages mount.

`ApiClient.forWorkspace(id)` captures an immutable, validated decimal-string company ID. It shares the root session, identity epoch, CSRF acquisition/rejection handling and session-expiry listeners. Resource requests always use `/api/v2/workspaces/{capturedId}`; auth/CSRF use v1. The shared resource key includes identity, company and request path. Leaving a company aborts outstanding requests; hooks ignore results from a disposed lifetime.

A save waiting for CSRF is checked for cancellation and identity change before sending. A write already sent can still commit in its original company; aborting the browser request cannot reverse a server transaction. Its completion cannot populate the newly opened company or navigate away from it. No write is retargeted or automatically replayed in the new company. Existing backend transaction checks remain the security boundary.

## Enable only in an isolated rehearsal

Use a migrated, reconciled disposable database and preserve the [scoped-write rollout requirements](workspace-writes.md). Do not enable against customer traffic while legacy routes can bypass company membership policy.

For a local preview, set these variables in their respective API/frontend terminals before starting the existing development commands:

```powershell
# API terminal
$env:WorkspaceReads__Enabled = 'true'
$env:WorkspaceWrites__Enabled = 'true'
dotnet run --project TaskFlow-BE/TaskFlow.Api

# Frontend terminal
$env:VITE_WORKSPACES_ENABLED = 'true'
npm run dev --prefix TaskFlow-FE
```

`VITE_WORKSPACES_ENABLED` is a public build-time boolean, never a secret. For a reviewed preview image use `docker compose --env-file .env.deploy -f compose.deploy.yaml build --build-arg VITE_WORKSPACES_ENABLED=true app`, and supply both backend flags in the rehearsal app's environment through a private Compose override. Runtime variables cannot enable a UI compiled without the flag. Normal images build with false; no production environment was enabled by this implementation.

## Verification and remaining gates

Three new MySQL HTTP tests cover flags/session/CSRF/body validation, two-company creation/visibility/suspension and transaction rollback. Client and UI tests cover scoped links, resource keys, cancellation while waiting for CSRF, already-sent writes, late results, identity changes, unavailable companies and default-off routes. The OpenAPI contract now contains 29 paths / 44 operations and the frontend consumes generated company DTOs.

The published HTTPS company journey is `node tests/browser/companies.cjs`; use the disposable setup and credentials in [browser verification](../../tests/browser/README.md). It creates companies/projects/tasks and checks switching, reloads, cross-company denial and desktop/mobile accessibility. It is separate from the default v1 browser suite, which keeps flags off in hosted CI until the coordinated preview job is configured. See [local evidence](../evidence/saas011-navigation.json).

Next is SaaS-012: company/project role lifecycle, suspension/leave, ownership transfer, assignment cleanup and company audit coverage. Company quotas, non-null scope, legacy route retirement, hosted CI/container evidence and the full acceptance/cutover remain open. Do not market this preview as completed tenant enforcement.
