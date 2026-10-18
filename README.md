# TaskFlow

Project and task management application using React/TypeScript, ASP.NET Core, and a planned MySQL database. The existing frontend and backend are starter projects. Business features and database access are not implemented yet.

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
- MySQL server target: 8.4.12. EF Core/Identity 10.0.11 and Oracle MySQL provider 10.0.9 are pinned and verified for schema generation. See [the toolchain guide](docs/toolchain.md) for the full matrix, local database recipe and remaining live-server checks.

## Run the current starters

From the repository root, restore and build:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet tool restore
dotnet build TaskFlow.slnx --no-restore
dotnet run --project TaskFlow-BE/TaskFlow.Api --launch-profile http
```

The HTTP API listens at `http://localhost:5140`. OpenAPI is `/openapi/v1.json` in Development. The weather template endpoint has been removed; business and health endpoints are introduced in later tasks. For the HTTPS profile use `--launch-profile https` (port 7258) and a trusted local development certificate. Certificate trust is a machine-level setup step.

In another terminal:

```powershell
cd TaskFlow-FE
npm ci
npm run dev
```

Open the local URL printed by Vite (normally `http://localhost:5173`). The starter frontend is not connected to the API yet. Proxy configuration, health endpoints, and standardized errors belong to backlog step 04.

## Verification

```powershell
dotnet test TaskFlow.slnx --no-restore
cd TaskFlow-FE
npm run typecheck
npm run build
npm run lint
```

The backend currently has one provider/Identity schema-generation compatibility test; it does not verify live MySQL or business behavior. The empty template test has been removed. Vitest and Testing Library are installed but have no configured test script or meaningful suite yet. Add behavior tests with the features and integration infrastructure in step 06.

Use `npm ci` with the committed package-lock.json for reproducible frontend installs. Do not commit secrets, local databases, build output, or dependencies. The workspace is a local Git repository on main. No remote is configured. Root ignore rules exclude local configuration, credentials and generated output; .env.example remains tracked.

## Implementation baseline

- [Architecture decisions](docs/decisions/README.md)
- [API contract amendments](docs/contracts/api-baseline.md)
- [Database contract amendments](docs/contracts/database-baseline.md)
- [Setup verification and outstanding prerequisites](docs/setup-review.md)
- [Architecture plan](Documents/Architecture-Plan.md)
- [Ordered delivery backlog](Documents/Delivery-Backlog.md)

The Markdown decision/contract documents refine the original Word specifications. Read them together; explicitly documented amendments take precedence for implementation. Steps 01 through 03 are complete. Next is step 04: add API conventions, health checks and the frontend development proxy. Live MySQL provisioning and migrations remain pending steps 05/06.
