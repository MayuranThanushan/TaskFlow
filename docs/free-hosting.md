# Free public pilot hosting checklist

Target: one Render Free Docker web service and a TiDB Cloud Starter database, owned by MayuranThanushan. No payment method, paid disk, worker, database upgrade or paid pre-deploy job is authorized. This is a pilot configuration; public deployment is not complete.

## 1. Connect the accounts

- [ ] Sign in to [Render](https://dashboard.render.com/login) using the intended GitHub account and authorize access to MayuranThanushan/TaskFlow. Supplying an email address does not authorize account access.
- [ ] Sign in to [TiDB Cloud](https://tidbcloud.com/signin) with the same owner and create a Starter instance on the free quota. Stop if either provider requests payment or a paid resource.
- [ ] Keep database credentials in a private password manager and hosting secret editor, never Git, build arguments, shared logs or chat.

Render Free sleeps after inactivity and provides no persistent disk. TiDB quotas and availability must be checked in the owner's dashboard. These services do not establish a company production SLA. See [Render Free limits](https://render.com/docs/free) and [TiDB tiers](https://docs.pingcap.com/tidbcloud/select-cluster-tier/).

## 2. Prepare the database

- [ ] Use an owner-controlled persistent Starter instance. The disposable Cloud Zero compatibility probe is synthetic test infrastructure, expires automatically, and must not receive real users.
- [ ] Obtain the dashboard's MySQL connection settings and require `SslMode=VerifyFull`; configure its allowed network access for the hosting service.
- [ ] `ConnectionStrings__TaskFlow` accepts either `Server=HOST;Port=4000;Database=test;User ID=USER;Password=PASSWORD;SslMode=VerifyFull;` or a `mysql://USER:PASSWORD@HOST:4000/test?ssl-mode=VERIFY_IDENTITY` URL. Use the actual database/port from TiDB. URL usernames/passwords must be percent-encoded when they contain special characters. Cloud URLs always use verified TLS; unsupported URL options are rejected. A shell command beginning `mysql -h ...` is not supported: replace it using the separate fields in TiDB's Connect screen. Keep the actual value private.
- [ ] Use a separate migration identity and least-privilege application identity following [runtime grants](../deploy/database-roles.sql.example). Review the managed service's supported privileges before applying grants.
- [ ] From the repository root, restore and build the exact revision being deployed:

```powershell
dotnet tool restore
dotnet restore TaskFlow.slnx --locked-mode
dotnet build TaskFlow.slnx -c Release --no-restore
```

- [ ] Set `ConnectionStrings__TaskFlow` privately in the current terminal to the migration connection. Do not put credentials in the command line or shell history. Apply the eleven migrations explicitly:

```powershell
dotnet ef database update --project TaskFlow-BE/TaskFlow.Infrastructure --startup-project TaskFlow-BE/TaskFlow.Api --configuration Release --no-build
Remove-Item Env:ConnectionStrings__TaskFlow
```

- [ ] Back up and rehearse recovery using [backup/recovery](backup-recovery.md); a free database is not a backup strategy. Do not import the local development database into the public pilot.

## 3. Prepare persistent encryption keys

- [ ] Generate keys once in a new private directory (choose another directory if it already exists):

```powershell
./scripts/New-HostedKeyRing.ps1 -OutputDirectory .local/hosting-keys/pilot-v1
```

- [ ] Restrict directory access to the operator on Windows. The output includes plaintext encryption key material encoded as Base64, not encrypted by Base64. Back it up privately with the database recovery material.
- [ ] Upload the contents of `key-ring.base64` directly into Render's encrypted `DataProtection__ReadOnlyKeyRingBase64` setting. Never print it to shared output. Reuse this ring across deployments and restarts.
- [ ] Schedule rotation before 90 days. Warning event `hosting.key_rotation_due` appears within 14 days of expiry. Generate a replacement retaining every old key:

```powershell
./scripts/New-HostedKeyRing.ps1 -OutputDirectory .local/hosting-keys/pilot-v2 -PreviousKeyRing .local/hosting-keys/pilot-v1/key-ring.base64
```

- [ ] Update the protected setting and redeploy before expiry. Retain old keys for encrypted MFA secrets and existing protected data; replacing the ring loses decryption capability. An expired-only ring prevents startup.

## 4. Create the web service

Prepared branch: [`hosting/free-render`](https://github.com/MayuranThanushan/TaskFlow/tree/hosting/free-render). [Open its Render deployment form](https://render.com/deploy?repo=https%3A%2F%2Fgithub.com%2FMayuranThanushan%2FTaskFlow%2Ftree%2Fhosting%2Ffree-render) after completing the database and key steps. Review the selected owner, branch and Free plan before submitting. The form does not constitute an already running deployment. The branch-specific link follows [Render's deployment shortcut documentation](https://render.com/docs/deploy-to-render).

The [Linux image workflow](https://github.com/MayuranThanushan/TaskFlow/actions/runs/42466013949) was submitted on 8 November 2026, but GitHub refused to start the job because the account is locked due to a billing issue. No Docker step ran. No payment details or billing settings were changed. Resolve the lock with GitHub support without adding paid resources, then rerun the workflow. It builds the combined image, checks non-root execution and frontend assets, generates temporary private keys inside a disposable container, and checks startup refusal without secrets. It publishes no image, cache, artifact or cloud resource.

- [ ] Review and push the hosting changes to an owner-controlled repository branch. Import [render.yaml](../render.yaml) as a Render Blueprint using that branch. Confirm the service says **Free** and no other resources are proposed.
- [ ] Set `ConnectionStrings__TaskFlow` to the application identity's verified-TLS connection and set the protected key ring. The manifest intentionally leaves both secrets unset.
- [ ] Keep automatic deployments off until the pilot is verified. No database migrations run during web startup.
- [ ] Build [deploy/render/Dockerfile](../deploy/render/Dockerfile). It serves React and the API together on the assigned port with a non-root runtime. Use the assigned `https://...onrender.com` origin, without a separate frontend host.

The opt-in Render ingress adapter accepts the HTTPS scheme only for the exact managed hostname and Render platform identity. It ignores forwarded host and client-IP headers. It relies on Render Free's public-edge-only ingress; do not reuse this mode for exposed container ports, private ingress or custom domains. Authentication limits conservatively aggregate by ingress socket peer until a trusted client-IP path is verified; assess this constraint before broader use.

## 5. Verify before inviting users

From `D:\TaskFlow`, set the actual assigned origin and warm the sleeping service, then run credential-free checks:

```powershell
$taskflowOrigin = 'https://YOUR-SERVICE.onrender.com'
Invoke-WebRequest "$taskflowOrigin/api/v1/health/ready" -UseBasicParsing -TimeoutSec 120 | Select-Object StatusCode
python scripts/smoke_deployment.py $taskflowOrigin --public-only
```

These checks perform GET/HEAD requests only and do not create accounts. For the authenticated restart/logout check, create a synthetic account in the browser, then enter its credentials privately and save a new session file:

```powershell
$taskflowSmokeCredential = Get-Credential -Message 'Synthetic pilot account only'
try {
    $env:TASKFLOW_SMOKE_EMAIL = $taskflowSmokeCredential.UserName
    $env:TASKFLOW_SMOKE_PASSWORD = $taskflowSmokeCredential.GetNetworkCredential().Password
    python scripts/smoke_deployment.py $taskflowOrigin --keep-session .local/free-hosting/hosted-session.cookies
    if ($LASTEXITCODE -ne 0) { throw 'Hosted authentication checks failed.' }
} finally {
    Remove-Item Env:TASKFLOW_SMOKE_EMAIL, Env:TASKFLOW_SMOKE_PASSWORD -ErrorAction SilentlyContinue
    $taskflowSmokeCredential = $null
}
```

Restrict access to the saved cookie file. Restart the service in Render without replacing its key secret, warm readiness again, then run:

```powershell
python scripts/smoke_deployment.py $taskflowOrigin --resume-session .local/free-hosting/hosted-session.cookies
```

The successful resume check logs out, rejects the saved cookie and removes the file. If verification fails, remove the private cookie file after investigating; never upload it as evidence. This script verifies transport/authentication; perform the two-company CRUD checks below separately.

- [ ] Confirm HTTPS readiness at `/api/v1/health/ready`, frontend deep links and secure session/CSRF cookies on the public origin.
- [ ] Register two synthetic accounts; test company isolation, project/task CRUD, stale edit conflicts and owner-only deletion.
- [ ] Restart/redeploy and confirm session continuity. Log out and confirm the saved session cannot be reused.
- [ ] Review structured logs without secret disclosure, cold-start behavior, database quota, backup restoration and operational ownership.
- [ ] Record the actual public URL and hosted results. Local rehearsals do not satisfy these hosted checks.

Invitations, email flows, MFA enrollment/enforcement and workers remain disabled in this pilot profile. Existing enrolled users are not migrated into it. TiDB `SKIP LOCKED` worker behavior is not certified; the upstream [locking issue](https://github.com/pingcap/tidb/issues/69720) prevents treating the MySQL worker path as compatible. Do not enable email/outbox workers without resolving and testing this separately.

Local evidence: the published HTTPS application passed eleven migrations against disposable TiDB, synthetic authentication and scoped CRUD, outsider rejection, version conflicts, soft deletion, restart continuity and logout revocation. Docker execution and owner-account public deployment remain release gates.

The local-to-cloud compatibility probe recorded several-second requests. It demonstrates behavior, not acceptable hosted latency. Select compatible regions and measure the actual hosted path before inviting users or making performance commitments.
