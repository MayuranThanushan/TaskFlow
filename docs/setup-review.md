# Initial setup review

Date: 18 October 2026. Scope: verify existing scaffolds and complete delivery step 01. This records observed local evidence; it does not certify unimplemented features.

## Verified

| Area | Evidence | Result |
| --- | --- | --- |
| Solution | Root TaskFlow.slnx includes Api, Application, Domain, Infrastructure, Tests | Correct |
| Dependencies | Api → Application/Infrastructure; Application → Domain; Infrastructure → Application/Domain | Matches architecture |
| Backend | All projects target net10.0; SDK 10.0.400; runtime 10.0.11 | Build passes with zero warnings/errors |
| Backend runner | xUnit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 | One provider compatibility test passes; no business coverage |
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

Package selection is now resolved by [decision 0004](decisions/0004-runtime-and-database-provider.md): keep .NET 10, use EF Core/Identity 10.0.11 and Oracle MySql.EntityFrameworkCore 10.0.9 (MySql.Data 26.7.0). The selected MySQL server target is 8.4.12. This explicitly supersedes the earlier proposed Pomelo provider. Exact dependencies and reproducible commands are in [toolchain.md](toolchain.md).

NuGet restore and backend build pass with zero warnings/errors. The provider/Identity schema-generation check passes; the empty template test was removed in step 03. Frontend build/lint pass with exact direct package pins. NuGet lockfiles cover every project; local dotnet-ef 10.0.11 is restored.

No MySQL/Docker command or MySQL Windows service was found in the current shell. No live database or migration has been tested. The optional compose.yaml and .env.example prepare local provisioning without supplying credentials; actual server/migration verification remains a step 05/06 prerequisite. Package compatibility and live database compatibility are tracked separately.

## Remaining scaffold work

| Item | Planned step | Current state |
| --- | --- | --- |
| Persistence version matrix and pins | 02 | Complete; see toolchain.md |
| Live MySQL environment and migrations | 05/06 | Recipe prepared; not provisioned or verified |
| Git initialization and initial commit | 03 | Complete: local main repository, no remote |
| Placeholder class/weather cleanup and first application shell | 03 | Complete: TaskFlow shell; unused templates/assets removed |
| API health, errors, logging, DI registration, development proxy | 04 | Not configured |
| Identity, application entities and migrations | 05 | Not implemented |
| Meaningful backend tests, MySQL integration, frontend test script, CI | 06 | Packages/scaffolds only |
| Auth, resource permissions and business features | 07 onward | Not implemented |

These items are deliberately tracked as pending. Existing builds establish a usable starting point; database and integration readiness remain unverified.
