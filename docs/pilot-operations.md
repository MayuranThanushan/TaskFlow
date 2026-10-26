# Pilot load and operating plan

SaaS-007, 26 October 2026. Local benchmark tooling and an initial baseline are available. Company sizing remains provisional; selected-host capacity, service objectives and recovery/alert delivery remain unverified. The [machine-readable profile](saas/pilot-operating-profile.json) distinguishes these proposals from enforced limits. This is not a customer SLA or a claim that multi-company isolation is shipped.

## Fixture and scope

| Profile | Users and data | Traffic | Status |
| --- | --- | --- | --- |
| Executable local baseline | Two disjoint project groups; four accounts, each group has one owner and one member; six projects and 120 tasks; mixed priority, assignment, status and nullable/overdue deadlines; activity created through actual mutations | Eight concurrent workers, 8 warm-up requests each, 8,000 measured requests; 87.5% reads and 12.5% task-status writes; hot parent project per group | Real current API; source fixture at tests/load/local-baseline.json |
| Proposed paid pilot | Ten companies, 25 active human users including guests per company, 25 simultaneously active users across the fleet; up to 20 active projects and 2,000 active tasks per company, at most 500 active tasks per project | Repeat representative read/write mix, then increase concurrency and introduce a large/hot company | Customer sizing approval pending. Requires workspace runtime and quota enforcement; not executed by the current fixture |

The fixture registers fresh synthetic accounts and creates data through the HTTPS API, including membership and activity. Preflight verifies each account sees only its group's projects and that another group's project/task IDs return 404. These checks exercise existing project privacy, not workspace boundaries. They do not replace the two-company/shared-user/guest/suspended-user matrix planned in SaaS-016.

Seeding, authentication, TLS/CSRF bootstrap and warm-up are outside measured latency. Every worker owns its own cookie jar and uses a validated HTTPS connection; no tokens, emails, cookies, IDs, raw request paths or bodies appear in reports. Latencies include response reading, client scheduling and connection/TLS overhead. urllib creates a connection for each request. This is a bounded closed-loop driver with no think time; throughput is observed completion rate, not a maximum capacity or an open-loop arrival-rate guarantee. Client saturation, coordinated omission, same-host networking and short duration limit conclusions. Server HTTP metrics measure a different boundary and must be compared separately on the target host.

Warm-up failures abort measurement. Any unexpected HTTP status, malformed/oversized response or transport failure during measurement counts as a failed sample, including 429 and timeouts (status 0); there are no automatic write retries. All samples, including failures, enter nearest-rank p50/p95/p99. Per-operation histograms prevent a fast route from hiding a slow one. The default command returns nonzero for any unexpected result. Proposed latency budgets are reported; `--enforce-budgets` additionally makes exceeded budgets fail the command. These short-run budgets are not production SLO evidence.

The fixture does not benchmark login storms, mail delivery, workers, attachments, multi-replica rate limiting, billing or future workspace APIs. Default setup uses eight register/login attempts from the same IP; the existing ten-per-minute limiter stays enabled. Run on a fresh isolated app, or wait for the auth window. A 429 aborts setup rather than disabling protection. Increasing registered users needs paced/operator-provisioned setup before a larger benchmark is meaningful.

## Repeat the local benchmark

Use a fresh disposable database, current migrations and a published Production-mode application following the [release runbook](release-runbook.md) and [deployment guide](deployment.md). Keep private connection/certificate/key files outside tracked source. The load command does not create/drop schemas, launch databases or change grants.

```powershell
# Point to an isolated published HTTPS app with its own disposable schema.
python scripts/pilot_load.py https://localhost:15173 --ca-file /private/certs/localhost.pem --disposable --output TestResults/load/baseline.json

# Optional real MySQL lock counters; defaults file contains restricted credentials.
python scripts/pilot_load.py https://localhost:15173 --ca-file /private/certs/localhost.pem --disposable --mysql /tools/mysql --mysql-defaults-file /private/monitoring.cnf --output TestResults/load/with-db-counters.json

# Fail on proposed latency budgets as well as unexpected responses.
python scripts/pilot_load.py https://localhost:15173 --ca-file /private/certs/localhost.pem --disposable --enforce-budgets

# Verify fixture safety, calculations, route mix and malformed-counter handling.
python -m unittest discover -s tests/operations -v
```

Remote hosts additionally require `--allow-remote`; use this only for an authorized isolated staging environment. Public certificates use the default trust store; never turn off certificate checks. Do not benchmark a customer/shared production database. Synthetic users and append-only activity remain after the run; soft-deleting projects is not cleanup. Stop the dedicated test app and destroy only its explicitly identified disposable schema through the operator's lifecycle procedure. The local verification harness did this after each run.

Optional database evidence reads `Innodb_row_lock_waits` and `Innodb_row_lock_time` from global status and enabled `lock_deadlocks` from INNODB_METRICS. Lock time is cumulative milliseconds; wait/deadlock values are counts. Snapshots bracket only the measured phase. These counters cover the whole MySQL server and may include unrelated traffic; run on an otherwise idle dedicated server for attribution. Missing/disabled counters or a reset cause failure; omitting MySQL arguments records `measured: false`, never invented zeros. Use a restricted monitoring account with access to the required metrics, not application credentials with unnecessary administrative privileges. Counter availability is described in the [MySQL 8.4 metrics manual](https://dev.mysql.com/doc/refman/8.4/en/innodb-information-schema-metrics-table.html).

## Measured local baseline

The verified run completed 8,000 requests in 19.584 seconds with eight workers and no unexpected results (408.496 completed requests/second). Overall p50/p95/p99 were 17.646/33.930/42.197 ms; status-write p95 was 39.805 ms. MySQL deltas were 269 row-lock waits, 1,875 cumulative milliseconds waiting for row locks, and zero deadlocks. Lock wait milliseconds overlap across requests and are not added to wall time. The same-host Windows/Python 3.13.9 driver reported 16 logical processors; this is neither a hardware entitlement nor a production capacity recommendation. Setup took 9.119 seconds and was excluded. Source/configuration and API-payload hashes are in the evidence file.

All 29 operations tests passed. The published app's clean migration/readiness, HTTPS/CSRF authentication, persisted-key restart, logout replay rejection and strict HTTP authentication policy also passed. The harness stopped the temporary app and removed its disposable schema. These results do not replace a sustained target-host run or a large-tenant data distribution.

## Proposed operating objectives

The initial proposal is 99.9% successful eligible requests over a rolling 28 days, 95% of reads within 750 ms and 95% of writes within 1,000 ms. These figures are product proposals pending host measurements and team acceptance. Separate read/write latency indicators count every eligible request in their denominator; successful responses within the threshold are good, so fast failures never improve the result. Availability uses a separate success ratio.

Before measurement, define eligible traffic at the ingress: intended authenticated business routes, valid requests and allowed access. Count infrastructure failures, 5xx, timeouts, connection failures and throttling below the agreed supported envelope as bad. Count expected validation/authentication/access denials in separate indicators; do not let anonymous probes or deliberate negative tests dilute the business denominator. External uptime/readiness probes must detect failures that never reach the application. Compare ingress and application counts, and alert on missing telemetry; absent metrics are not zero errors. Aggregate histogram buckets across instances before calculating latency. Never average per-instance percentiles.

These eligibility rules require ingress/prober configuration; today's coarse request/error instruments alone cannot classify all of them. Do not declare the SLO implemented from application 5xx counts. The error budget is the allowed fraction of bad eligible requests, not a promised number of outage minutes. When an approved budget is exhausted, pause discretionary rollouts and prioritize reliability; emergency/security fixes require incident-owner review. This follows the measurement and release-policy approach in the [Google SRE workbook](https://sre.google/workbook/error-budget-policy/); our numeric proposals are not prescribed by that source.

Existing [backup objectives](backup-recovery.md) remain proposed RPO 24 hours and RTO 60 minutes, with daily/pre-upgrade backups and seven daily/four weekly retained copies. Host scheduling, encryption, off-host copies, key-ring recovery and a timed restoration are launch gates. No backup automation or recovery guarantee is added by the benchmark.

## Accountability and alert handling

The user selected a **designated developer or operations team** to own production alerts and recovery. That team owns the backup schedule, restore rehearsals, incident response, maintenance and release go/no-go. A named primary responder, backup responder, escalation destination and coverage hours must be recorded before pilot onboarding; these identities and external routing have not been supplied or configured. Product/customer support communicates impact and approves changes to customer objectives.

Proposed starting alerts, pending host calibration:

| Signal | Initial trigger | Operator action |
| --- | --- | --- |
| External readiness | Two consecutive failed probes, 30 seconds apart | Check host/network/DB, correlate safe trace IDs, assess incident scope; avoid restart loops |
| Business failures | Over 1% bad requests for five minutes with at least 100 eligible requests; external probes cover low traffic | Triage recent changes, throttle overload and consider reviewed rollback |
| Slow reads/writes | Per-class p95 above proposed threshold for ten minutes with adequate sample count | Inspect CPU, memory, connection pool and DB contention/query plans; do not log SQL parameters or tenant labels |
| Database contention | Any deadlock increment, sustained rising lock waits/time or connection exhaustion | Triage transaction ordering and blocked writers; compare to baseline; do not kill transactions blindly |
| Backup freshness | No verified scheduled backup within 26 hours or any failed pre-upgrade backup | Block rollout, restore backup service and verify a recoverable copy |
| Telemetry loss | Collector ingestion/probe gap beyond two expected intervals | Fix the measurement path; treat unknown health separately from healthy |

These are proposed alert rules, not deployed alerts. Before launch, inject a failed probe, collector outage and backup failure, verify the named primary receives each alert, exercise escalation to the backup, and record acknowledgment and recovery times without sending customer data.

## Required host and tenancy follow-up

1. Agree pilot counts and access/data distributions; approve atomic quota behavior in SaaS-016. Current API page/field bounds and login rate limiting are enforced, but company seat/project/task quotas are not.
2. Select the staging host under SaaS-005 and record CPU/RAM, DB size/storage/connection limits, app replicas, image/source digest, TLS/network topology, collector settings and driver host. Verify recovery and alert ownership there.
3. After SaaS-008 through SaaS-016, seed the proposed companies plus shared identities, guests, skewed large projects, deleted resources and retained activity. Re-run adversarial access checks before generating load.
4. Run warm-up, steady-state and a 30-minute soak at agreed concurrency. Step concurrency (for example 5, 10, 25, 50) while recording offered/completed traffic, memory/CPU/DB pool, p95/errors, lock waits and deadlocks. Record generator saturation and timeout distribution. Test bursts, slow DB/collector failure and recovery without masking failures with retries.
5. Accept capacity only at an explicitly supported envelope with repeatable headroom, no isolation failures, no unhandled deadlocks and acceptable latency/errors. Approve quotas and objectives with the operating team. Keep SaaS-007's host/approval items open until this evidence exists.

See [local evidence](evidence/saas007-load.json) for the measured baseline and its limitations.
