# TaskFlow

Project and task management application using React/TypeScript, ASP.NET Core, and MySQL. The API foundation, Identity persistence, application entities and initial MySQL migration are implemented. Account endpoints and project/task workflows are still pending.

## Workspace

```text
TaskFlow.slnx
TaskFlow-BE/
  TaskFlow.Api/                 HTTP endpoints and composition root
  TaskFlow.Application/         Use cases, DTOs, and interfaces
  TaskFlow.Domain/              Entities and business rules
  TaskFlow.Infrastructure/      Persistence and Identity implementations
  TaskFlow.Tests/               Existing xUnit project
TaskFlow-FE/                   React application, Vite, TypeScript
Documents/                     Original requirements and delivery plan
docs/
  decisions/                   Architecture decisions
  contracts/                   Amendments to API/database source documents
  setup-review.md              Verified setup and remaining gaps
```

Keep these directory names. Dependency direction is Application → Domain; Infrastructure → Application/Domain; Api → Application/Infrastructure. The existing references already follow this rule.

## Local prerequisites

- .NET SDK 10.0.400 (the root global.json allows compatible SDK patches).
- Node 22.15.0 and npm 11.3.0 are the verified local versions. `TaskFlow-FE/.nvmrc` records Node; package.json declares supported engine ranges.
- MySQL server target: 8.4.11. EF Core/Identity 10.0.11 and Oracle MySQL provider 10.0.9 are pinned and verified against live MySQL. See [the toolchain guide](docs/toolchain.md) for the full matrix and [database setup](docs/database-setup.md) for completed live-server checks and local database instructions.

## Run the current starters

From the repository root, restore and build:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet tool restore
dotnet build TaskFlow.slnx --no-restore
dotnet run --project TaskFlow-BE/TaskFlow.Api --launch-profile http
```

The HTTP API listens at `http://localhost:5140`. OpenAPI is `/openapi/v1.json` in Development. The weather template endpoint has been removed; GET /api/v1/health/live reports process liveness; business endpoints arrive in later tasks. For the HTTPS profile use `--launch-profile https` (port 7258) and a trusted local development certificate. Certificate trust is a machine-level setup step.

In another terminal:

```powershell
cd TaskFlow-FE
npm ci
npm run dev
```

Open the local URL printed by Vite (normally `http://localhost:5173`). Vite forwards relative /api requests to the backend. Verify with http://localhost:5173/api/v1/health/live. See [API foundation](docs/api-foundation.md) for proxy overrides, health semantics, trace IDs and error responses.

## Verification

```powershell
dotnet test TaskFlow.slnx --no-restore
cd TaskFlow-FE
npm run typecheck
npm run build
npm run lint
```

The backend has 16 passing tests when a dedicated TASKFLOW_TEST_CONNECTION is configured: nine API/provider checks and seven live MySQL checks. Without that connection the seven live tests are explicitly skipped. See [database setup](docs/database-setup.md) for migration commands and local server instructions. Vitest and Testing Library are installed but have no configured test script or meaningful suite yet. Add behavior tests with the features and integration infrastructure in step 06.

Use `npm ci` with the committed package-lock.json for reproducible frontend installs. Do not commit secrets, local databases, build output, or dependencies. The workspace is a local Git repository on main. No remote is configured. Root ignore rules exclude local configuration, credentials and generated output; .env.example remains tracked.

## Implementation baseline

- [Architecture decisions](docs/decisions/README.md)
- [API contract amendments](docs/contracts/api-baseline.md)
- [Database contract amendments](docs/contracts/database-baseline.md)
- [Setup verification and outstanding prerequisites](docs/setup-review.md)
- [Architecture plan](Documents/Architecture-Plan.md)
- [Ordered delivery backlog](Documents/Delivery-Backlog.md)

The Markdown decision/contract documents refine the original Word specifications. Read them together; explicitly documented amendments take precedence for implementation. Steps 01 through 05 are complete. Next is step 06: shared MySQL test fixtures, frontend test infrastructure and CI. The local verification databases are migrated; their portable server is stopped.
