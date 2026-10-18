# 0004: Runtime and database provider

Status: adopted. Date: 18 October 2026. Delivery step: 02.

## Decision

Keep net10.0 and SDK 10.0.400. Use Microsoft EF Core/Identity EF persistence/design tooling 10.0.11, Oracle MySql.EntityFrameworkCore 10.0.9 and its locked MySql.Data 26.7.0 dependency. Target MySQL Community Server 8.4.12 (8.4 LTS line), InnoDB, utf8mb4 with utf8mb4_0900_ai_ci collation. Preserve Domain/Application independence from persistence packages.

This explicitly replaces the SAD's proposed Pomelo provider. [Oracle's package metadata](https://www.nuget.org/packages/MySql.EntityFrameworkCore/10.0.9) declares a net10.0 asset requiring EF Core/Relational >=10.0.9; the chosen 10.0.11 packages satisfy that range. [Pomelo's compatibility table](https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql#compatibility) lists EF Core 9 for its 9.0.0 release. Avoid mixing Pomelo 9 with EF Core 10 or downgrading the user's runtime. [MySQL's 8.4 release notes](https://dev.mysql.com/doc/relnotes/mysql/8.4/en/) list the selected server patch. Recheck availability/security advisories before actually provisioning it.

The Microsoft package patch matches the existing ASP.NET Core runtime/OpenAPI baseline. It is an explicit tested baseline, not a claim that 10.0.11 is the newest patch. Review updates together rather than changing unrelated versions during scaffolding.

Keep existing frontend dependencies; replace semver ranges with their already resolved package-lock versions. Record npm@11.3.0 as packageManager. Enable NuGet lockfiles for all projects, commit packages.lock.json, use locked-mode restore in CI and npm ci for frontend installs. Pin the local dotnet-ef tool to 10.0.11 in the tool manifest; no global EF tool required.

## Evidence and limits

Restore and build succeeded with no warnings/errors. A provider compatibility test creates a long-key Identity model and generates MySQL schema SQL without opening a network connection. This exercises the actual EF/provider/Identity assemblies and SQL generation. Frontend production build and lint pass after exact pinning.

These checks do not establish live-server compatibility, migration application, timezone round trips, locking behavior or data integrity. No Docker/MySQL command or MySQL service is available in the current shell. The optional local database recipe is prepared in compose.yaml; live MySQL checks remain required in steps 05/06. No database, users, migrations, credentials or external service were created in this task.

## Implementation consequences

Oracle's EF configuration extension is `UseMySQL`, not Pomelo's `UseMySql`. Provider-specific configuration, annotations, migrations and integration tests must use Oracle APIs. Authentication remains ASP.NET Core Identity with long keys as previously decided. Before the first real migration, verify unique normalized-email indexes, required text lengths, DATETIME(6), enum constraints, restrictive FK deletes and soft-delete visibility against the actual server.
