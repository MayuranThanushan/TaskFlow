# Initial setup review

Date: 18 October 2026. Updated through delivery step 07. This records observed local evidence; it does not certify unimplemented features.

## Verified

| Area | Evidence | Result |
| --- | --- | --- |
| Solution | Root TaskFlow.slnx includes Api, Application, Domain, Infrastructure, Tests | Correct |
| Dependencies | Api â†’ Application/Infrastructure; Application â†’ Domain; Infrastructure â†’ Application/Domain | Matches architecture |
| Backend | All projects target net10.0; SDK 10.0.400; runtime 10.0.11 | Build passes with zero warnings/errors |
| Backend runner | xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 | 30 tests pass with live MySQL; 15 run without it |
| Backend runtime | Start compiled API in Development on an isolated port; request /openapi/v1.json | HTTP 200 and valid OpenAPI after template cleanup |
| Frontend | React/DOM 19.3.1, Router 7.18.4, Axios 1.20.0, TypeScript 6.0.3, Vite 8.3.4 | Production build and ESLint pass |
| Frontend runtime | Node 22.15.0, npm 11.3.0 | Installed Node satisfies local Vite/Vitest/jsdom engine declarations |
| Frontend startup | Start Vite on an isolated port; request / | HTTP 200 and expected application entry HTML |
| Frontend reproducibility | Existing package-lock.json and installed dependency tree | npm ls reports no invalid top-level dependencies |

## Foundation improvements in this task

- Preserve the user's TaskFlow-BE/TaskFlow-FE and root solution paths.
- Add root .gitignore for backend/frontend output, local configuration, credentials and IDE files.
- Pin SDK feature band with global.json and record Node in .nvmrc and frontend package engines.
- Add root setup instructions and explicit current limitations.
- Replace default logging providers with console/debug providers: the Windows Event Log provider threw an access-denied exception on a normal HTTP warning during the runtime check.
- Adopt three architecture decisions and API/database amendments; update planning documents to match current workspace and step 01 completion.

## Database compatibility gate

Package selection is now resolved by [decision 0004](decisions/0004-runtime-and-database-provider.md): keep .NET 10, use EF Core/Identity 10.0.11 and Oracle MySql.EntityFrameworkCore 10.0.9 (MySql.Data 26.7.0). The selected MySQL server target is 8.4.11. This explicitly supersedes the earlier proposed Pomelo provider. Exact dependencies and reproducible commands are in [toolchain.md](toolchain.md).

NuGet restore and backend build pass with zero warnings/errors. 35 tests pass with a dedicated MySQL test connection: 19 API/provider/deployment/safety checks and 16 live persistence/auth/readiness checks. The empty template test was removed in step 03. Frontend build/lint pass with exact direct package pins. NuGet lockfiles cover every project; local dotnet-ef 10.0.11 is restored.

No Docker/MySQL CLI was on PATH initially. Step 05 provisioned Oracle portable MySQL under ignored .local/mysql, with separate local development/test credentials. The initial migration was applied to both isolated databases and live constraints verified. The server is stopped; no Windows service was installed. Compose remains an unexecuted alternative.

## Remaining scaffold work

| Item | Planned step | Current state |
| --- | --- | --- |
| Persistence version matrix and pins | 02 | Complete; see toolchain.md |
| Live MySQL environment and migrations | 05 | Complete: portable 8.4.11, migrated isolated dev/test DBs; server stopped |
| Git initialization and initial commit | 03 | Complete: local main repository, no remote |
| Placeholder class/weather cleanup and first application shell | 03 | Complete: TaskFlow shell; unused templates/assets removed |
| API health, errors, logging, DI registration, development proxy | 04 | Complete; see api-foundation.md |
| Identity, application entities and migrations | 05 | Complete; see database-setup.md |
| Shared database fixtures, frontend test script, CI | 06 | Complete: fixture guards, Vitest setup and validated GitHub workflow |
| Account API and frontend authentication | 07, 08 | Complete; see authentication.md and frontend-authentication.md |
| Portable deployment and smoke rehearsal | 09 | Prepared and verified locally; container/selected-host checks pending |
| Resource permissions and business features | 10 onward | Not implemented |

Only rows marked pending remain unimplemented. Initial migration and live persistence verification are complete; shared integration infrastructure was completed in step 06.

Step 04 verification: backend build (zero warnings/errors), nine tests, frontend type-check/build/lint and live Vite-to-API health/error proxy checks pass. Details: [API foundation](api-foundation.md).

## Step 05 update

The earlier live-database gaps described above are now resolved for initial migration verification. MySQL 8.4.11 was downloaded from Oracle, checksum-verified, and run on loopback with isolated dev/test accounts. The initial migration applied to both databases; 16 tests passed without skips, and EF reports no pending model changes. The temporary server was stopped. Shared fixture lifecycle, frontend testing and CI remain step 06. See [database setup](database-setup.md).

## Step 06 update

Shared xUnit collection initialization and safety guards passed against MySQL. Mandatory mode fails instead of skipping when its connection is absent. Frontend Vitest/JUnit checks, type-check/lint/build pass. Backend Release tests produced TRX and Cobertura reports with 22 passes and no skips. CI passed actionlint validation; the hosted workflow has not run because no remote is configured. See [testing](testing.md).

## Step 07 update

Registration, login, current-user and logout APIs are implemented with cookie sessions, global CSRF validation and default controller authorization. Thirty backend tests pass with live MySQL, two frontend tests and type/lint/build pass, and certificate-verified HTTPS proxy/session-restart checks pass. No migration changes are pending. Local processes are stopped and generated passwords/certificates/keys remain ignored. See [authentication](authentication.md).

## Steps 08 and 09 update

Frontend account screens, protected navigation, session lifecycle and API/CSRF handling pass 24 frontend tests and real Edge HTTPS checks. Step 09 prepares portable app/migration images, private MySQL and mounted secrets/keys. The combined published app passed a Production HTTPS rehearsal with a clean database, idempotent migration bundle, readiness transitions, SPA deep links, browser authentication and session recovery after restart. All 35 backend tests pass with live MySQL and no skips. Docker is unavailable, so container execution and a selected public host remain pending release gates; Compose and the expanded workflow passed configuration/actionlint validation. See [deployment](deployment.md). Next feature task is step 10.
