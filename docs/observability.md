# Service metrics and trace export

SaaS-004 implementation, 26 October 2026. [Metric and security vocabulary contract](contracts/telemetry.json). [Local verification evidence](evidence/saas004-observability.json). This is operational telemetry, separate from [project activity and future attributed company audit](saas-architecture.md).

## What is available

The API publishes eight instruments through the .NET Meter `TaskFlow.Api`:

| Instrument | Meaning |
| --- | --- |
| taskflow.http.requests | Request count by normalized method, registered MVC route template and status |
| taskflow.http.errors | Request failures classified as client_error, server_error or cancelled |
| taskflow.http.request.duration | Pipeline duration histogram in seconds; includes failed requests |
| taskflow.database.readiness.checks | Probe executions by a finite safe result code |
| taskflow.database.readiness.duration | Probe duration histogram in seconds |
| taskflow.database.readiness | Last completed probe: -1 unknown, 0 not ready, 1 ready |
| taskflow.database.readiness.age | Seconds since completed probe; -1 before a completion |
| taskflow.security.signals | Operational authentication/authorization action and outcome counts |

Readiness metrics change when /api/v1/health/ready is probed. This does not add a database polling service: configure an operator health probe and monitor the age. Cancellation preserves the last completed readiness state. Liveness remains independent of MySQL. Readiness still verifies exact migration equality and readable Identity data, with no schema changes or writes.

A status dimension of 0 denotes an aborted/unknown response, not an HTTP status sent to a client. Unhandled exceptions count as 500 even before a response is available. Normal completed 4xx are separate from server failures. Metrics include the application pipeline, including auth, redirects and static/unmatched traffic; unmatched paths collapse to one dimension. Durations exclude browser/render time and downstream delivery after the pipeline returns. Histograms use the contract's second-based buckets; metrics are not sampled.

No /metrics endpoint or additional public port is introduced. Local diagnostics can attach to the .NET Meter using an operator-installed diagnostic tool. The existing authenticated application and health routes retain their contracts.

## Optional OTLP export

Export is off by default in every environment. Only `Telemetry:Otlp:Enabled` opts in; ordinary OTEL exporter environment variables do not enable the application exporter. Enabling requires a base collector URL. The app sends OTLP HTTP/protobuf to base-url/v1/traces and base-url/v1/metrics. Do not configure a frontend URL or append a signal path to the base yourself. No collector account, hosted vendor or paid service is provisioned.

For a local collector that you already run, set these in the API terminal before following the manual [README startup commands](../README.md):

```powershell
$env:Telemetry__Otlp__Enabled = 'true'
$env:Telemetry__Otlp__Endpoint = 'http://127.0.0.1:4318/'
$env:Telemetry__Otlp__AllowInsecureLoopback = 'true'
$env:Telemetry__TraceSampleRatio = '1'
```

Loopback HTTP requires the explicit opt-in above. Remote collectors require verified HTTPS. URL credentials, query strings and fragments are rejected; redirects are disabled and TLS validation remains standard. Collector transport does not change the application's independent HTTP/HTTPS authentication policy. Turn export off with `Telemetry__Otlp__Enabled=false` and restart the API; setting changes apply at startup.

| Configuration | Default / bounds |
| --- | --- |
| Telemetry:Otlp:Enabled | false |
| Telemetry:Otlp:Endpoint | Required when enabled; base HTTPS URL or explicitly allowed loopback HTTP |
| Telemetry:Otlp:AllowInsecureLoopback | false |
| Telemetry:Otlp:Headers | Empty; optional comma-separated collector key=value authentication headers |
| Telemetry:TraceSampleRatio | 0.1; finite 0-1; 1 is useful for verification, 0 disables sampled request traces |
| Telemetry:Otlp:TimeoutMilliseconds | 3000; whole number 100-30000 |
| Telemetry:Otlp:MetricIntervalMilliseconds | 30000; whole number 5000-300000 |

Store collector authentication headers in a restricted secret file named `Telemetry__Otlp__Headers` under the existing TASKFLOW_SECRETS_DIRECTORY or in the host secret manager. Write no trailing newline; CR/LF values are rejected. Never commit header values or put tokens in URLs. Configuration validation errors withhold supplied values. The disabled configuration ignores endpoint/header values entirely and creates no network exporters.

The hosted export service initializes after final configuration is assembled. Traces use a bounded queue of 2048 spans and batches of 256, scheduled every five seconds. SDK export and reader timeouts are bounded. Shutdown makes a best-effort flush, followed by SDK disposal. Collector refusal/outage must not fail business requests; this is best-effort telemetry and may drop data. ForceFlush means queue processing completed, not that a downstream backend persisted the data. Protect and monitor the collector separately; there is no durable telemetry spool or delivery guarantee in this task.

## Privacy and correlation

Only the custom TaskFlow.Api source/meter is exported. No automatic ASP.NET URL capture, SQL/statement instrumentation, request/response body recording, header collection or application log export is enabled. Spans include fixed request fields or a readiness result; no exception messages/stacks, private titles, search text, raw paths, account/company/resource IDs, cookies or credentials. Incoming tracestate is cleared, and baggage is not mapped to exported tags. Only a valid W3C traceparent is reused for distributed correlation; it never grants access.

Request spans share the X-Trace-Id/Problem Details correlation already used in logs. With sampling, some log correlations have no exported span. Sampling uses the configured trace-ID ratio independently of incoming sampled flags; it is not an abuse-control mechanism. Metrics never use trace IDs as dimensions, and metric exemplars are disabled.

Fleet dimensions are allowlisted by the SDK view: http.request.method, http.route, http.response.status_code, error.type, result, event.action and event.outcome. Instrument producers restrict their values to registered route templates and finite vocabularies. Unknown HTTP methods become _OTHER. SDK streams have a 512-point cardinality limit and can aggregate overflow. A regression test injects an unapproved workspace.id dimension and confirms it is dropped. No customer/user/project ID labels are allowed.

Resources identify taskflow-api, the API assembly informational version and a recognized deployment environment (otherwise Other), plus SDK metadata where applicable. Hostnames, process identity and arbitrary environment resource attributes are not added. This task does not enable an external auto-instrumentation agent; additional sources, tags or detectors require a separate privacy review.

## Security signals and future worker metrics

The versioned contract defines authentication.login, identity.registration, authentication.logout, authentication.session_rejected and authorization.denied. Outcomes are success, rejected, conflict, rate_limited and error. These are counts of observed HTTP outcomes; a login 400 is not asserted to be a CSRF incident, and a 404 does not reveal whether a private resource exists. Expected anonymous session probes can increment session_rejected. No actor ID, IP address, email, audit event ID or customer attribution is emitted by these signals. Durable, attributed and access-controlled security events remain SaaS-013.

The same JSON contract reserves worker.ready, outbox.pending, outbox.oldest.age, delivery.attempts and outbox.dead_letter instruments for SaaS-017. They are marked planned and are not emitted as invented zero/healthy values. When implemented, define lease health, queue eligibility, bounded channel/outcome dimensions, deduplication and the distinction between attempts and delivered messages before enabling alerts.

## Operator verification and release gates

1. With export disabled, confirm normal liveness/authentication and absence of exporter traffic.
2. Point the API at an approved collector; verify both OTLP signal endpoints accept data and the service/version/environment are correct. Use sampling 1 only for the controlled verification window.
3. Exercise a successful request, validation/authentication failure, and a readiness failure/recovery. Follow an X-Trace-Id into the collector and the corresponding operational log.
4. Check histograms/counts, readiness state and age, and confirm no private canaries or tenant labels are present. Check application and collector data access/retention policies.
5. Stop/reject collector delivery and confirm application requests still work; restore delivery and check recovery. Missing telemetry must alert on the collector/ingestion side rather than look like zero errors.

The automated integration suite starts a temporary real loopback HTTP/protobuf receiver, verifies both signal payloads and response-trace correlation, injects private canaries, checks an unapproved metric dimension is removed, and exercises collector rejection. It does not certify a hosted collector, its durable storage, TLS deployment, authentication, dashboards or alert routing. The existing frontend bundle is untouched.

Useful dashboard definitions: request rate; 5xx ratio with a nonzero request denominator; 4xx/cancellation separately; p50/p95 histogram latency; readiness state plus probe age; authentication rejection/rate-limit counts. Establish thresholds, traffic exclusions, alert owner/escalation, retention and production capacity in SaaS-005/007. Do not present local timings as an SLA. Before declaring production monitoring operational, attach selected-host evidence, collector delivery/absence alerts and a named responder to the release record.

Primary references: [OpenTelemetry .NET exporters](https://opentelemetry.io/docs/languages/dotnet/exporters/), [Microsoft metric instrumentation](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation), [OTLP exporter configuration](https://opentelemetry.io/docs/languages/sdk-configuration/otlp-exporter/).
