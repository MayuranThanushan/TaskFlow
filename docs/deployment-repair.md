# Repair the Render database readiness failure

The 15 November 2026 logs show a successful container build followed by repeated `database.schema_mismatch` and readiness HTTP 503. The connected database's EF migration history differs from the image. This is not a frontend install/build failure. The current checkout expects sixteen migrations. Missing migrations, a different selected database or a newer database paired with an older image can all cause this code.

The repair keeps the database readiness gate. Render must receive HTTP 200 from `/api/v1/health/ready` before the release is accepted; changing the health path to liveness would hide the broken schema.

## Error 1142 and a connection ending in /sys

`sys` is a TiDB system schema, not an application database. Do not apply TaskFlow migrations there or grant additional system-schema privileges. The repair tool now refuses sys, mysql, information_schema, performance_schema and metrics_schema before making a connection; readiness also rejects a system-schema target.

If a full connection/password has been shared in chat or logs, reset that SQL user's password first in TiDB -> cluster -> Settings -> SQL Users -> the user's action menu -> Edit. Update all affected application secrets privately; never share the replacement.

In the cluster SQL Editor, using an authorized database-administration account, create the application database:

```sql
CREATE DATABASE IF NOT EXISTS taskflow;
```

Use the same host and appropriate user with the rotated password, but change the URL database suffix from `/sys` to `/taskflow` (or `Database=sys` to `Database=taskflow` in .NET format). Set that application database in **both** the hidden migration prompt and Render's `ConnectionStrings__TaskFlow`, using the appropriate migration/runtime identities. Then rerun the repair and deploy the matching commit. The tool does not create a database or change account privileges automatically.

Sources: [TiDB sys schema](https://docs.pingcap.com/tidbcloud/sys-schema/), [create a database](https://docs.pingcap.com/developer/dev-guide-create-database/), [SQL user/password management](https://docs.pingcap.com/tidbcloud/configure-sql-users/).

## Day-one checklist

- [ ] Open PowerShell on your own computer and get the matching code:

```powershell
Set-Location D:\TaskFlow
git switch main
git pull --ff-only origin main
```

- [ ] In Render, open this service -> Environment and identify the **server and database** used by `ConnectionStrings__TaskFlow`. Keep the value private. In TiDB -> your cluster -> Connect, obtain credentials permitted to apply schema migrations to that same database. Use a connection string / `mysql://` URL, not the `mysql -h ...` terminal command. Prefer a separate migration account; Render should keep the restricted runtime account.

Accepted input shapes (replace placeholders privately):

```text
Server=HOST;Port=4000;Database=YOUR_DATABASE;User ID=MIGRATION_USER;Password=PASSWORD;SslMode=VerifyFull;
mysql://ENCODED_USER:ENCODED_PASSWORD@HOST:4000/YOUR_DATABASE?ssl-mode=VERIFY_IDENTITY
```

For URLs, percent-encode special username/password characters. Remote maintenance refuses unverified TLS. Do not paste filled credentials into a command line, chat, Git or shared logs.

- [ ] Check the schema without modifying it:

```powershell
.\scripts\Repair-HostedDatabase.ps1
```

The script builds its own tool under `.local`, then asks for the private TiDB migration connection with hidden input. It does not start the API, require hosting keys or overwrite the connection used by your running backend. It ignores an unrelated inherited API secrets directory and restores process configuration when it exits.

Read the JSON result: `pendingMigrationCount` and `pendingMigrations` identify known missing changes. `unexpectedMigrationCount` means this database contains migration IDs unknown to this checkout. Exit 2 means pending migrations; exit 3 means unknown history. Unknown history blocks updating: verify the database/revision, and do not delete or fabricate `__EFMigrationsHistory` rows.

- [ ] Retain a verified [backup/recovery copy](backup-recovery.md), then apply pending forward migrations:

```powershell
.\scripts\Repair-HostedDatabase.ps1 -Apply
```

Paste the same private migration connection when prompted. Success ends with `database.migration_completed` or `database.schema_current`, `pendingMigrationCount: 0` and `unexpectedMigrationCount: 0`. Repeating the update on a current schema is a no-op. MySQL/TiDB DDL may not be transactional: if a migration fails, run the status check before retrying. A failed partial DDL needs inspection; do not reset an existing user database or mark missing work as applied.

- [ ] Apply matching runtime grants from [database-roles.sql.example](../deploy/database-roles.sql.example) using an approved administrative connection. The web process must not receive migration privileges just to make startup work. Keep feature/worker flags off until their selected-host checks pass.

- [ ] In Render -> your TaskFlow service -> Manual Deploy -> Deploy latest commit, deploy the same revision used by the repair tool. No build-cache clearing or paid resource is required for this repair. Older binaries can remain unready after the schema advances until the matching binary is deployed.

- [ ] Check the assigned HTTPS URL, replacing the placeholder:

```powershell
$taskflowOrigin = 'https://YOUR-SERVICE.onrender.com'
Invoke-WebRequest "$taskflowOrigin/api/v1/health/ready" -UseBasicParsing -TimeoutSec 120 | Select-Object StatusCode
python scripts/smoke_deployment.py $taskflowOrigin --public-only
```

Expected: readiness 200 and the public smoke checks pass. Startup warnings for the inherited HTTP port and redundant in-app HTTPS redirect are also corrected by this revision. Strict authentication HTTPS checks remain enforced; the validated Render edge owns redirection. See [Microsoft's proxy/HTTPS guidance](https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl?view=aspnetcore-10.0) and [Render health checks](https://render.com/docs/health-checks).

## Optional local CA certificate

With the public ISRG Root X1 file on Windows, use:

```powershell
.\scripts\Repair-HostedDatabase.ps1 -CaFile 'D:\TaskFlow\deploy\certs\isrgrootx1.pem'
.\scripts\Repair-HostedDatabase.ps1 -Apply -CaFile 'D:\TaskFlow\deploy\certs\isrgrootx1.pem'
```

Enter the rotated `/taskflow` migration URL at the hidden prompt. `-CaFile` preserves verified TLS and hostname checks. The wrapper restores prior CA configuration when it exits. The certificate is public; the database password remains private. Do not configure the Windows `D:\...` path in Render's Linux container: Render uses its system CA trust store unless a separate Linux certificate path has been provisioned. See [TiDB verified TLS connections](https://docs.pingcap.com/tidbcloud/secure-connections-to-serverless-clusters/).

## Existing private environment / published runtime

For an automation terminal where you intentionally set the migration connection privately, use `-UseExistingConnection`. This is explicit so a local development connection is not accidentally selected by the normal hosted repair prompt:

```powershell
.\scripts\Repair-HostedDatabase.ps1 -UseExistingConnection
.\scripts\Repair-HostedDatabase.ps1 -UseExistingConnection -Apply
```

A matching published API DLL also supports these offline commands without an SDK, web listener, hosting keys or cookie configuration:

```powershell
dotnet TaskFlow.Api.dll --database-status
dotnet TaskFlow.Api.dll --database-update
```

Supply its migration connection through environment or a dedicated private key-per-file directory. Do not change the Render web start command to a maintenance command. Normal web startup never migrates automatically.

If the repair fails, share only its safe `code`, migration counts and `databaseErrorNumber` (if present), not the connection. The tool intentionally withholds raw exception strings. This repository fix has been rehearsed locally; applying it to the actual TiDB instance requires the owner's private connection and the resulting hosted readiness check.
