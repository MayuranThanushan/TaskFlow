# Toolchain baseline

Initial selection: 18 October 2026; refreshed 26 October 2026 after merged dependency updates. Step 02 completed for dependency selection, pinning and package compatibility. Step 05 subsequently verified the initial migration on portable MySQL 8.4.11; see [database setup](database-setup.md).

| Component | Selected version | Source of truth |
| --- | --- | --- |
| .NET SDK | 10.0.400, latest compatible SDK patch allowed | global.json |
| Target framework | net10.0 | Backend project files |
| ASP.NET Core runtime baseline / OpenAPI package | 10.0.11 / 10.0.14 | Installed runtime / Api project |
| EF Core and Relational | 10.0.11 | Infrastructure reference and NuGet locks |
| Identity EF persistence | 10.0.11 | Infrastructure project |
| ASP.NET HTTP test host | 10.0.11 | Tests project and NuGet lock |
| EF Design and dotnet-ef | 10.0.11 | Api project / .config/dotnet-tools.json |
| MySql.EntityFrameworkCore | 10.0.9 | Infrastructure project |
| MySql.Data | 26.7.0 | NuGet transitive lock |
| MySQL server target | 8.4.11 | compose.yaml; portable server verified in step 05 |
| Node / npm baseline | 22.23.4 / 11.3.0 | .nvmrc / packageManager |
| React / React DOM | 19.3.1 | package.json and package-lock.json |
| React Router / Axios | 7.18.4 / 1.20.0 | package.json |
| TypeScript / Vite | 6.0.3 / 8.3.4 | package.json |
| Vitest / jsdom | 5.0.5 / 30.1.3 | package.json |

All other direct frontend packages are exact pinned in package.json; transitive versions are locked. Backend transitive versions/hashes are locked per project. Preserve all five NuGet lockfiles. See [decision 0004](decisions/0004-runtime-and-database-provider.md) for provider rationale and primary sources.

## Reproduce package verification

From the workspace root:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet tool restore
dotnet ef --version
dotnet build TaskFlow.slnx --no-restore
dotnet test TaskFlow.slnx --no-build --no-restore
```

From TaskFlow-FE:

```powershell
npm ci
npm run build
npm run lint
```

Package restore needs network access on a fresh machine. The empty template test was removed in step 03. ProviderCompatibilityTests verifies schema SQL generation, not a live database. Do not treat this passing test as feature coverage.

## Local database recipe for the persistence step

Docker is optional; native MySQL matching the target is also valid. Docker must be installed and running before using the supplied Compose recipe. Copy `.env.example` to `.env` and set two different local-only passwords; no usable credentials are supplied. Then run from the root:

```powershell
docker compose up -d mysql
docker compose ps
```

The database binds only to 127.0.0.1:3306, uses a named volume, and creates taskflow_dev with a non-root taskflow user. Health status checks server responsiveness; later integration checks must verify actual authenticated queries. Do not put the root password in application configuration.

The application connection string uses Oracle Connector/NET syntax: `Server=127.0.0.1;Port=3306;Database=taskflow_dev;User ID=taskflow;Password=<local value>`. Supply it through `ConnectionStrings__TaskFlow` in local process configuration/user secrets, or a mounted configuration file as described in [deployment](deployment.md). Remote hosting requires TLS and separately provisioned credentials.

Use a separate taskflow_test database/credentials for integration tests. Do not point test reset or migration commands at shared/production databases. The Compose recipe has not been executed here because no Docker CLI/daemon is available. Step 05 instead verified Oracle portable MySQL 8.4.11, applied the migration and ran live tests. Validate the Docker image pull separately when provisioning with Compose.

Node 22.23.4 replaces the old 22.15.0 baseline because jsdom 30.1.3 and its transitive dependencies require a newer runtime. Frontend engines require ^22.22.2, ^24.15.0 or >=26; .nvmrc and the container build pin the verified 22.23.4 release. Use the pinned npm 11.3.0. The merged OpenAPI update also requires refreshing the Tests project transitive lock; all solution restores must still use --locked-mode. Local Node archive checksums were checked against the [official release checksums](https://nodejs.org/dist/v22.23.4/SHASUMS256.txt).
