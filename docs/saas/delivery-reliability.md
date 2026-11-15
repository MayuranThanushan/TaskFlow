# SaaS-029: delivery reliability, revocation races and load verification

15 November 2026. Local reliability release. No schema migration, public API, frontend change, new dependency or hosted service is introduced. Sixteen migrations remain required. Existing watcher/scheduler and email rollout flags stay unchanged and off in the free profile.

## Dispatch authority and revocation

An invitation lease reserves work; it does not grant permission to send. After claiming, the worker now reacquires the company share lock, then the invitation row lock, and checks the current message identity, lease token/expiry, pending state, invitation expiry and inviter/project authority **before decrypting or invoking a sender**. The same transaction retains these locks through the provider call and finalization. Normal company/invitation writers acquire the company gate first too.

A revocation that commits before this final gate suppresses the send. A send that already owns the gate is ordered before a subsequent revocation; the revocation waits, then cancels the invitation/token. Already accepted external mail cannot be recalled. The dispatch phase has a 25-second cancellation deadline and its sender call a 20-second deadline (the SendGrid adapter additionally has its existing 10-second transport deadline). Cancellation rolls back finalization, retaining the earlier durable lease for bounded expiry/recovery. Holding the share lock can delay company writes while dispatch is in progress; selected-host lock-wait and transaction-deadline verification is required before enabling delivery.

In-app scheduling retains its existing all-or-nothing company transaction, SKIP LOCKED behavior and recipient/event unique keys. A revocation transaction that owns the gate makes the competing scheduler report Busy. On the next batch, inaccessible work is suppressed. Watcher subscription revisions and audited intervening-revocation fences still prevent old work from reviving after re-subscription or restored access. These guarantees cover normal application writers; direct privileged database modifications must follow the same locking/audit contract.

## Reliability semantics

- Inbox insertion and delivered marker commit together. Transaction failure retries without duplicating a recipient/event row.
- An external provider acceptance and the database acknowledgment cannot commit atomically. A crash/timeout after possible acceptance can produce a duplicate email on retry. Message identity stays stable across retries; SendGrid custom arguments are correlation metadata, not an idempotency guarantee. ProviderAccepted does not mean mailbox delivery.
- Retries keep the existing five-attempt bound, backoff and bounded provider Retry-After handling. Caller cancellation leaves reclaimable work; terminal captured/accepted jobs are not replayed. Dead-letter mail payloads are cleared, so recovery uses an authorized fresh invitation resend/account request rather than resetting queue columns.
- Checkpoint outbox replay remains bounded and metadata-only. It never starts email delivery or discovers new recipients. No user-facing replay endpoint or automatic purge is added.

## Private queue observation

Use a separate read-only operations identity with only the commented `taskflow_delivery_monitor` grants in [database-roles.sql.example](../../deploy/database-roles.sql.example). Do not reuse root, runtime, email or scheduler credentials. Configure its `ConnectionStrings__TaskFlow` privately through the environment or a dedicated secret directory. No email key, Data Protection ring or content access is needed.

```powershell
Set-Location D:\TaskFlow
dotnet publish TaskFlow-BE/TaskFlow.Worker --configuration Release --output .local/releases/delivery-monitor /p:RestoreLockedMode=true /p:UseAppHost=false
New-Item -ItemType Directory -Force .local/monitoring | Out-Null

dotnet .local/releases/delivery-monitor/TaskFlow.Worker.dll --delivery-status |
    Set-Content -Encoding UTF8 .local/monitoring/delivery-health.json
if ($LASTEXITCODE -ne 0) { throw 'Queue observation failed; investigate worker configuration/schema/grants.' }
python scripts/check_delivery_health.py .local/monitoring/delivery-health.json
```

The explicit command checks migration equality, then samples checkpoint, account mail, invitation mail and watcher queues under a read-only consistent transaction with a 20-second deadline. It emits versioned JSON, a database UTC observation time and global aggregate counts only. There are no customer/resource labels, recipients, private titles, tokens or protected payloads. It does not claim that zero pending work means a scheduler or provider is healthy.

`pending` includes pending and leased work; `due` includes available pending work and expired leases. `oldestDueAgeSeconds` measures eligible delay, excluding future quiet/digest work. `retrying` counts pending/leased mail or checkpoint jobs with more than one attempt, not a provider error rate. `deadLetter` is unresolved retained terminal history and continues alerting until an approved operational disposition is made; the checker does not clear it. Invitation counts consider only invitations still Pending. These are full aggregate scans; calibrate observation frequency and cost on the selected host, and archive history only through an approved retention policy.

The checker exits **0** for a fresh healthy snapshot, **2** for firing alerts and **1** for invalid/unavailable observation. It rejects incomplete inventories, invalid/non-finite values and stale observations (older than 180 seconds or more than 60 seconds in the future). Failed/missing observation is never translated into healthy zeros. Schedule the probe and checker together, initially every minute. An external supervisor must also alert on absence of the probe itself; this repository does not install a scheduler, collector or alert-routing service.

## Alert response checklist

The initial thresholds below are pilot defaults, not a hosted SLO. The designated developer/operations team owns triage; record a named primary and backup before rollout. Route checker output and nonzero exit status through the team's existing private monitoring system. Do not attach raw connection strings, message content or provider response bodies.

| Code / initial trigger | Action |
| --- | --- |
| delivery.observation_unavailable / failed or malformed sample | Check worker exit status, exact schema revision, network and separate monitor grants; fix observation before assessing delivery. |
| delivery.observation_stale / older than 180s or excessive clock skew | Check probe supervision, database/host UTC clocks and ingestion; run a fresh probe. |
| delivery.backlog / at least 100 due rows | Check the corresponding worker/feature flags, cadence and capacity; drain bounded batches and verify due count falls. Future quiet/digest rows do not trigger it. |
| delivery.overdue / oldest due row at least 300s old | Inspect safe failure codes, provider reachability/rate limiting and database lock waits. Restore service and verify eligible age falls. |
| delivery.lease_expired / any expired lease | Check process exits/timeouts and supervisor; let the correct worker reclaim through its normal lease protocol. Never remove lease fencing or acknowledge an old token. |
| delivery.retry_pressure / at least 25 multiply attempted active jobs | Inspect safe provider codes and Retry-After, account configuration and dependency health; avoid manual hot-loop retries. |
| delivery.dead_letter / any retained dead letter | Stop blind retry. Review scoped metadata with the approved operations identity, fix the cause, then use bounded checkpoint replay or an authorized new mail request. Preserve attempt/audit history. Record disposition and alert acknowledgment privately. |

Before enabling on a selected host, inject each failure into a disposable company and verify notification receipt by the primary, escalation to backup, acknowledgment and recovery. Local evaluator fault injection verifies codes/exit statuses and recovery only; it does not prove a human received an alert. Keep scheduling disabled on TiDB until the company share/exclusive locks, SKIP LOCKED, timeout/rollback, revocation races and restricted roles pass there. No live provider, hosted CI, 30-minute soak, large-tenant fairness or selected-host capacity approval is claimed.

## Reproduce verification

CI's existing backend job runs the new tests against its disposable MySQL 8.4 service and runs the operations tests. Locally, privately set `TASKFLOW_TEST_CONNECTION` to a dedicated non-root `taskflow_test_*` database and require execution:

```powershell
$env:TASKFLOW_REQUIRE_MYSQL = 'true'
dotnet test TaskFlow.slnx --configuration Release --filter 'FullyQualifiedName~DeliveryReliability|FullyQualifiedName~Outbox|FullyQualifiedName~SendGridEmailTests|FullyQualifiedName~Onboarding|FullyQualifiedName~Watcher'
python -m unittest discover -s tests/operations -v
```

Tests synchronize at the committed claim boundary rather than using sleeps. They verify revoke/suspend-before-send, cancellation and expired-lease recovery, accepted-but-uncommitted ambiguity, rate-limited retry/revocation, scheduler Busy/suppression, and a 220-intent backlog generated by ten real API writes for 22 authorized recipients. The load uses 24 active company members, respects the ten-write/minute limiter, runs competing scheduler batches of at most 100 rows, and asserts exact recipient/source uniqueness and no cross-company delivery. The two-minute test budget is a local regression guard, not a latency objective or a full-pilot soak. See [release evidence](../evidence/saas029-reliability.json).

Next: SaaS-030 board read model with status counts and per-column pagination.

SaaS-030 now implements the next [board read-model foundation](board-read-model.md). Reliability rollout gates remain unchanged; next product implementation is SaaS-031 rank/moves.

<!-- current-documentation-index -->
Current implementation, release boundaries and rollout gates: [documentation index](../documentation-index.md). Release-specific counts and follow-ups above describe their recorded release.
