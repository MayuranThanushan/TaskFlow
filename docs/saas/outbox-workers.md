# Transactional outbox and checkpoint worker

SaaS-017, 3 November 2026. Scoped business mutations now append immutable, ID-only outbox intent and initial delivery work in the same transaction as business data and its success audit. A separately hosted worker leases committed work, records immutable attempt transitions and acknowledges an internal metadata checkpoint. No email, inbox notification, webhook or customer-facing delivery adapter is enabled.

## Producer and scope

Approved capture points are ProjectTransactions (after generated resource IDs and typed mutation records are prepared), WorkspaceLifecycle.Write and WorkspaceDirectory.CreateAsync. Capture uses structured action identifiers and workspace/actor/project/task references, never parsed activity summaries, raw Details, before/after content, email, credentials or bearer URLs. Project.* and Company.* prefixes distinguish the two business/governance fact streams; a single operation may produce several distinct facts. Unknown kinds fail enqueue and roll back the mutation. Direct operational DbContext writes do not implicitly publish events.

Each OutboxEvent has a server-generated stable MessageId, numeric database ID, schema version 1, kind, workspace/actor references, optional project/task references, UTC occurrence and server correlation. OutboxDelivery has one unique (EventId, Consumer) work row; this release registers only checkpoint.v1. Initial availability uses the database UTC default. Composite actor/company, project/company, task/project/company, event/delivery and delivery/attempt references prevent cross-company metadata. A check requires a task reference to include its parent project. Intent and attempt rows are append-only in the application and protected by separate DB grants. Delivery state is mutable only through the worker role.

Only newly committed scoped records are captured. Legacy unscoped v1 writes, global identity/security events and historical audit rows are not backfilled into deliveries. Scope/admission, resource revision and quota failures produce no successful outbox event. An enqueue/audit failure rolls back earlier generated business rows as well. This is part of the existing transaction, not fire-and-forget work after the HTTP response.

These v1 envelopes are metadata checkpoint facts, not recipient-bound send authority or complete content snapshots. They contain no rendered message or recipient address. Do not convert the historical checkpoint backlog into email sends. Future recipient-bound events/consumers must explicitly define subject/recipient intent, schema, current membership/project authorization, consent/preferences, expiry and protected token handling. No current worker is allowed to read user accounts or private project/task content.

## Lease, attempts and recovery

The store claims one eligible checkpoint delivery using a short READ COMMITTED transaction and SELECT ... FOR UPDATE SKIP LOCKED. It commits a random fencing token, 60-second lease, monotonic attempt number and Started transition before consumption. No DB transaction/lock is held during the handler. Database UTC controls scheduling and expiry; the TTL starts after the row lock is obtained. Acknowledgement obtains the same scoped row lock and then samples database time again, so waiting for a lock cannot revive an expired lease. It verifies workspace, consumer, current token, Leased state and non-null/unexpired expiry before any update.

A successful checkpoint marks Succeeded and records a success transition atomically. Duplicate/stale/foreign acknowledgements return false. A thrown/timeout consumer failure schedules retries after 5, 10, 20 and 40 seconds; five attempts per round lead to DeadLetter. Unsupported schema/kind is a permanent message.unsupported failure. Each expired claim records LeaseExpired; expiry after the fifth round attempt dead-letters rather than executing another handler. Failure codes are a closed vocabulary; exception/provider text is neither persisted nor logged. Immutable transitions preserve failure reasons after replay.

A worker batch handles at most 25 claims; the daemon polls every five seconds. A terminal expired claim may end the current batch, with remaining work handled on the next poll. Consumption has a 20-second deadline. Future handlers must honor cancellation and must never assume cancellation undoes a provider side effect. Shutdown leaves unfinished leases to expire/recover. The store requires a clean isolated context and clears tracked snapshots before locking operations, preventing an older EF identity-map entry from bypassing fencing.

Privileged replay applies only to a checkpoint DeadLetter row in the supplied workspace, at most three replays. Round attempts reset; lifetime attempts and MessageId never reset. Pending/Succeeded/foreign/wrong-consumer or over-limit replay fails. Replay appends a timestamped transition; operators must record their human identity/change ticket in the deployment operations audit. The CLI/DB service account is not a customer authorization mechanism and has no public replay endpoint.

One internal receipt is protected by the unique work row, transaction and fencing checks. Consumption is **at least once**: a crash between external side effect and acknowledgement can repeat a provider call. Future external adapters must use stable message/channel/recipient idempotency keys and tolerate ambiguity. This release promises neither exactly-once external delivery nor strict per-tenant/resource ordering or fairness.

## Run the separate process

First migrate with the migration identity and apply [producer grants](../../deploy/database-roles.sql.example) and the separate [worker grants](../../deploy/worker-roles.sql.example). The eighth migration is `20261103143655_TransactionalOutbox`. It creates three empty tables and two explicit project/task composite FKs, with no historical event replay or business-data rewrite. MySQL DDL is not an online rollback mechanism; use maintenance, verified backup/keys and forward repair or restore. Stop old writers/workers during the coordinated deployment.

Build and publish from the repository root:

```powershell
dotnet restore TaskFlow.slnx --locked-mode
dotnet publish TaskFlow-BE/TaskFlow.Worker -c Release --no-restore -o .local/releases/worker
```

Provision a private secrets directory outside the published app, containing `ConnectionStrings__TaskFlow` with the separate worker connection string. Restrict its ACL; do not pass credentials on the command line or reuse runtime/migration/backup identities. The process checks exact compiled/applied migration equality and never migrates on startup.

```powershell
$env:TASKFLOW_SECRETS_DIRECTORY = 'D:\TaskFlow\.local\worker-secrets'
# One bounded batch; rerun to process remaining eligible work.
dotnet .local/releases/worker/TaskFlow.Worker.dll --once
# Long-running process; stop with Ctrl+C / service termination.
dotnet .local/releases/worker/TaskFlow.Worker.dll
# Privileged replay: workspace ID then delivery ID, from reviewed metadata.
dotnet .local/releases/worker/TaskFlow.Worker.dll --replay 123 456
```

Exit 0 means the bounded command completed, not that a customer was notified or all backlog drained. Replay returns 2 when refused; configuration/cycle failure in once mode returns 1 with safe diagnostics. There is no HTTP listener. Deploy the long-running process under an operations-owned supervisor with stdout collection and the selected-host resource/alert policy.

For the optional portable container, use [Dockerfile.worker](../../deploy/Dockerfile.worker) and [compose.worker.yaml](../../compose.worker.yaml) with the existing deployment compose file. Create a private worker_connection secret in TASKFLOW_DEPLOY_SECRETS_DIR after provisioning that DB identity, migrate first, then opt in:

```powershell
docker compose -f compose.deploy.yaml -f compose.worker.yaml --profile worker up -d --build worker
```

The worker profile is not enabled by default, has no published port, joins only the database network and runs unprivileged with a read-only root, tmpfs and resource bounds. Containers/selected-host execution remain unverified locally; run the deployment gates on the chosen host.

## Monitoring and operator review

The worker emits JSON stdout with static event IDs, counts and safe failure types, not payloads, credentials, lease tokens or provider exception text. Meter TaskFlow.Outbox exposes the [worker telemetry contract](../contracts/worker-telemetry.json): readiness, pending+leased count, oldest pending/leased age, attempt counter and dead-letter count, without tenant/entity dimensions. Readiness stays 0 before successful sampling and after a failed cycle. Queue gauges are last-observed samples, not invented zero values while DB access is unavailable. No process means no measurements; monitor the process/target as well. The API does not emit fake worker instruments. Configure actual metric collection/export and alerts on the selected host; instrument existence is not alert delivery evidence.

A restricted operator can review metadata with a bounded query (no private content joins):

```sql
SELECT Id, WorkspaceId, EventId, Consumer, State, Attempts, Replays, LastFailureCode
FROM OutboxDeliveries WHERE Consumer='checkpoint.v1' AND State='DeadLetter'
ORDER BY Id LIMIT 100;
```

Use immutable attempt rows for replay/failure history. The worker can read only outbox metadata/history and migration history, update whitelisted delivery-state columns and append transitions. It cannot insert source events, alter source/history, delete rows, read accounts/security records or access private project/task content. The API producer can insert source/initial work and read only generated ID/default-availability columns; it cannot lease or read event payloads. Retention/archive/queue-storage budgets and tenant dispatch fairness remain reviewed future controls; no automatic queue purge or legal-hold bypass is added.

## Verification and limits

Live MySQL tests cover atomic enqueue/rollback, ID-only canaries, competing claims, expiry/restart, stale/foreign/duplicate acknowledgements, a synchronized acknowledgement lock wait, retry/backoff, bounded replay, malformed versions, scoped FK rejection, immutable history and consumer failure sanitization. Metrics tests check actual names/units/state and zero tenant dimensions. A published HTTPS rehearsal uses real restricted producer and worker accounts, two independent worker OS processes and bounded restarts, proving no duplicate internal receipts and negative content/source/history permissions. A populated synthetic restore upgrades to eight migrations and confirms no historical outbox work appears before any new writes.

See [release evidence](../evidence/saas017-outbox-workers.json). HTTP inventory remains 40 paths / 57 operations; no frontend delivery/inbox UI is shipped. Hosted CI/CodeQL/container verification, deployment grants, supervisor, backlog/storage/fairness load, collector/alerts, recovery and coordinated tenant cutover remain acceptance gates. Next: SaaS-018 email abstraction/templates/local capture and staged delivery, with provider/recipient security reviewed before any real sending.
