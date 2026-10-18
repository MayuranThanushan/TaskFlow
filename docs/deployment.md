# Portable deployment and smoke verification - step 09

No hosting provider or public environment has been selected. The portable configuration and local Production rehearsal are complete. Docker execution and public-host verification remain pending; nothing has been published. These checks remain release requirements even though work can continue with step 10.

## Deployment shape

One ASP.NET Core process serves the compiled React application and `/api/v1` on the same HTTPS origin. Production does not run Vite or need CORS. Client deep links serve `wwwroot/index.html`; API/OpenAPI misses remain JSON errors. Hashed `/assets` files are immutable for one year, while the entry document revalidates. CSP, content-type protection and referrer policy are applied. Production enables HSTS with ASP.NET Core's default local-host exclusions. Auth requests over HTTP are rejected without redirect; other HTTP requests redirect when an HTTP listener and HTTPS port are configured.

`deploy/Dockerfile` has versioned Node/npm and .NET build stages, a non-root runtime image, and a separate non-root migration-bundle image. `compose.deploy.yaml` is independent of the development/test Compose files. It provisions private MySQL 8.4.11, persistent database and protection-key volumes, read-only application filesystems and writable temporary directories. Only HTTPS is published, bound to loopback by default. The API uses direct TLS; an external proxy must pass TLS through or re-encrypt to the app. TLS termination with forwarded headers is not configured or trusted implicitly.

MySQL must pass an authenticated `SELECT 1` health check before migration/application startup. Migrations are an explicit one-shot job, not part of web startup. Apply them successfully before starting the app. `/api/v1/health/ready` returns 200 `{ "status": "Healthy" }` only when MySQL connects, no compiled migrations are pending, and the Identity table can be read. Failure returns 503 `{ "status": "Unhealthy" }`, without database diagnostics. It never creates a schema or changes data. `/api/v1/health/live` remains database-independent. Both are public and use no-store caching; readiness has a five-second health-check timeout.

## Required deployment inputs

Copy `.env.deploy.example` to ignored `.env.deploy` and edit the non-secret host, bind address, port and absolute secrets-directory path. The hostname has no scheme/port and must match the TLS certificate. Keep loopback binding for local verification. A selected public host can explicitly bind its HTTPS interface and configure DNS/firewall/port 443 later.

Provision these files outside version control under `TASKFLOW_DEPLOY_SECRETS_DIR`. Text values must be UTF-8 without BOM or trailing newlines:

| File | Contents |
| --- | --- |
| `mysql_password` | Generated password for the non-root `taskflow` database user |
| `mysql_root_password` | Different generated administrative password; never mounted into the app |
| `ConnectionStrings__TaskFlow` | `Server=mysql;Database=taskflow;User ID=taskflow;Password=<mysql_password>` plus the chosen database TLS policy |
| `tls.pfx` | Certificate/private-key bundle for the public hostname |
| `Kestrel__Certificates__Default__Password` | PFX password |

Local Compose secrets are file mounts, not a managed encrypted secret store. On Linux, keep the parent directory private to its operator (0700) and mounted files readable by the non-root container user (for example 0444 inside that private directory). File-source Compose secrets preserve host file permissions; do not assume a requested UID/mode will fix unreadable files. Only grant host operators access to the source directory. Provision the PFX through the chosen certificate authority and renew it before expiry. Use the existing development certificate only for local rehearsal.

The app loads named files from `TASKFLOW_SECRETS_DIRECTORY` after normal configuration; mounted values win over environment values. Double underscores represent configuration sections. Binary PFX files and dotfiles are ignored by this provider. Secrets/certificates are never build arguments, frontend variables or image contents. Restart the app to pick up replaced secret files.

Keep MySQL private. For a remote database, select the host's verified TLS policy/CA (Connector/NET `SslMode=VerifyFull` with its CA), restrict access and separate schema-migration privileges from runtime access. The single-host Compose baseline uses the initialized database account for both; least-privilege production grants, managed secrets, encrypted storage and backup automation remain release/environment work. The CI database is disposable and isolated, with no production credentials.

## Build, migrate and start on a Docker host

Run from the repository root after provisioning secrets. These commands use Docker Compose v2. BuildKit builds the app and migration images from the same source. The migration bundle targets the selected Linux amd64/arm64 architecture; only a Linux x64 bundle has been generated locally so far.

```sh
docker compose --env-file .env.deploy -f compose.deploy.yaml --profile tools config --quiet
docker compose --env-file .env.deploy -f compose.deploy.yaml --profile tools build app migrate
docker compose --env-file .env.deploy -f compose.deploy.yaml up -d --wait --wait-timeout 180 mysql
docker compose --env-file .env.deploy -f compose.deploy.yaml --profile tools run --rm migrate
# Continue only if migration exits successfully.
docker compose --env-file .env.deploy -f compose.deploy.yaml up -d app
```

Use the readiness URL to wait for the app itself; Compose's app service has no internal HTTP probe. The MySQL health gate is not an application-readiness guarantee. Review migration SQL and back up existing data before upgrades; a failed migration must not be followed by app rollout. Running the migration job again is idempotent. Stop with `docker compose --env-file .env.deploy -f compose.deploy.yaml down`; keep both named volumes and the same Compose project name. Removing the database volume loses data; removing the key volume invalidates existing sessions. Full backup/recovery rehearsal and release rollout/rollback procedures belong to step 22.

The default key directory is `/var/lib/taskflow/keys`, owned by the image's non-root app user and mounted as a named volume. Protect that host volume and its backups with restricted access and encryption at rest; this configuration does not encrypt the XML key ring itself. Retain its contents across app replacement. The current in-process authentication limiter is for one API instance; multi-replica deployment needs a deliberate limiter/proxy policy.

## Publish without containers

The same runtime layout can be produced on Windows or a PowerShell-enabled build host:

```powershell
./scripts/Publish-App.ps1
```

This performs locked backend restore/publish and `npm ci` plus the production frontend build, then copies `dist` into the new release's `wwwroot`. It defaults to a new ignored `.local/releases/<UTC timestamp>` directory and refuses an existing output directory. Set `-OutputDirectory` to another new location if needed. Run `dotnet TaskFlow.Api.dll` with that release as the working/content directory and a compatible .NET 10 ASP.NET Core runtime. Configure `ASPNETCORE_ENVIRONMENT=Production`, an HTTPS listener/certificate, `AllowedHosts`, database connection and an explicit persistent `DataProtection__KeysPath`. Optionally set an absolute `TASKFLOW_SECRETS_DIRECTORY` for file configuration. Apply migrations deliberately before routing traffic. See [local HTTPS setup](authentication.md#local-https-setup) for development certificate export; publishing never embeds those settings.

## Smoke check

Python 3's standard library is sufficient. Use a dedicated smoke account because logout revokes all of that account's sessions. Supply `TASKFLOW_SMOKE_EMAIL` and `TASKFLOW_SMOKE_PASSWORD` in the process environment without putting credentials into command-line arguments. Normally the account already exists. `--register` explicitly creates a disposable account and is intended only for isolated verification; there is no public account-deletion endpoint.

```sh
python3 scripts/smoke_deployment.py https://your-host.example
```

For a private test CA/certificate, add `--ca-file /path/to/public-ca.pem`; verification stays enabled. The script rejects HTTP origins and unexpected redirects. It checks readiness/liveness, compiled assets/cache policy, SPA deep links/HEAD, JSON API misses, unavailable production OpenAPI, CSRF rejection, login/cookie flags, current user, logout and revoked-cookie replay.

For a restart/replacement rehearsal, first add `--keep-session .local/restart-session.lwp`. Recreate the application using the same database and key volume, wait for readiness, then run the same command with `--resume-session .local/restart-session.lwp`. Successful resumption verifies the existing session before logout and removes the session file. This artifact contains a sensitive cookie: keep it private and remove it after a failed/abandoned check too. It is never uploaded by CI.

## Verification evidence and remaining gates

Local checks passed:

- Release build, locked restore, 35 backend tests with real MySQL and zero skips, including production routing, API/static separation, security/cache headers, HTTPS policy, readiness failure and live readiness success.
- A clean, disposable database initially returned readiness 503 while liveness stayed 200. A generated Windows migration bundle applied the initial schema; a second run was a no-op, and readiness became 200.
- The published app ran in Production from its release directory, with database/PFX-password settings loaded from files and persisted keys outside the release. The committed smoke script verified HTTPS, assets/deep links, cookie/CSRF behavior, process restart and revoked-cookie rejection.
- Headless Edge completed register, failed/successful login, return URL, reload and logout against the published app. Desktop/mobile screenshots were inspected, no page errors occurred and 390px layout did not overflow. The browser pinned the development certificate's public key; protocol TLS verification stayed enabled. No machine trust settings changed.
- Linux x64 and Windows x64 migration bundles built. Official checksum-verified Compose v2.40.3 validated the configuration; actionlint v1.7.12 validated the workflow. Generated releases, tools, keys, secret files and browser artifacts remain ignored. Verification processes stopped and the disposable deployment database/accounts were removed.

Docker is unavailable on this workstation, so image builds, container permissions/networking and container recreation have not been executed locally. The new **Deployment smoke** GitHub job builds both images, starts isolated MySQL, applies migrations, runs the same HTTPS smoke script, recreates the app with its existing key volume, verifies session recovery/revocation, and removes only its disposable CI services/volumes. The hosted job remains unrun because no remote is configured. It does not publish images or an application.

Once a target is selected, run the container job and verify public DNS/TLS/renewal, host readiness checks, deep links, API routing, private database access, mounted secrets/key permissions, persisted-session replacement and backups in that actual environment. This is a pending release gate, not evidence of a public deployment.

Implementation references: [ASP.NET Core static files](https://learn.microsoft.com/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0), [file configuration](https://learn.microsoft.com/aspnet/core/fundamentals/configuration/?view=aspnetcore-10.0#key-per-file-configuration-provider), [EF migration bundles](https://learn.microsoft.com/ef/core/managing-schemas/migrations/applying#bundles), and [Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/).
