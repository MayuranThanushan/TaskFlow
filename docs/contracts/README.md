# Typed API contract and drift verification

SaaS-003 foundation, updated through SaaS-026. [openapi.json](openapi.json) describes the implemented v1 API plus the opt-in v2 features: 63 paths / 82 operations. It is generated from controllers, application DTOs and [OpenAPI transformers](../../TaskFlow-BE/TaskFlow.Api/Contracts/ContractOperationTransformer.cs). [Generated TypeScript](../../TaskFlow-FE/src/api/generated.d.ts) supplies the frontend's wire models and operation definitions. Keep the [API amendments](api-baseline.md) alongside the machine contract for transaction, visibility, trimming and ordering rules.

The snapshot documents success DTOs, nullable required response fields, presence-aware task PATCH bodies, query names/defaults/allowlists, Problem Details, correlation/cache headers and cookie/CSRF security. Both transport cookie alternatives are described: Local uses TaskFlow.Auth/TaskFlow.Csrf, Strict uses __Host-TaskFlow.Auth/__Host-TaskFlow.Csrf. These alternatives describe deployment configuration, not two interchangeable sessions accepted by one deployment. Login/register require CSRF but no authenticated session. Health and GET CSRF are public. Production still hides /openapi and has no Swagger UI.

Canonical representations use application/json; problems use application/problem+json. IDs remain decimal strings. Counts are numbers. PATCH omission preserves a value; null clears only description, assigneeId and dueDateUtc; empty/unknown/duplicate fields are rejected. A documentation-only request shape describes the existing JsonElement parser without replacing it or losing presence information. Required response properties can contain explicit null; optional request properties may be omitted.

Typed body binding accepts case variants of canonical camelCase names; the dynamic task PATCH parser requires exact field names. Enums are case-sensitive. Collection query names are case-insensitive at runtime; the document uses canonical camelCase names. The API additionally checks signed BIGINT numeric range, timestamp parsing/UTC year range, trimming, authorization and cross-field pagination/date rules that are not completely expressible in these schemas. Generated types are compile-time checks, not runtime validation or authorization. They cannot enforce a nonempty PATCH object by themselves.

SaaS-026 extends the existing preferences GET/PUT with opt-in reminder/digest settings and adds TaskDue/TaskDigest kinds and explicit InAppDelivered status. Scheduling fields must be supplied together or omitted to preserve settings. Path/operation counts remain unchanged. See [scheduling rules](../saas/notification-scheduling.md).

## Generate a reviewed contract change

SaaS-025 adds notification preference GET/PUT and bounded POST bulk read (63 paths / 82 operations). Preference revision 0 represents absent defaults; only the current user/company can change it. See [inbox semantics](../saas/notification-inbox.md).

SaaS-024 adds three recipient-scoped notification operations (61 paths / 79 operations): list, unread count and PATCH read state. All require current company/project access and the default-off notification flag. See [notification semantics](../saas/notifications.md).

SaaS-023 adds `ProviderAccepted` to invitation delivery state, distinct from local `Captured` and recipient delivery. Invitation creates/resends share 20/hour per company; budget exhaustion returns `429 invitation.send_limited` with bounded `Retry-After`. No new HTTP routes or database migration are added. See [onboarding acceptance](../saas/onboarding-acceptance.md).

Run from the repository root. The isolated artifacts directory avoids rebuilding DLLs loaded by a running local API. Generation uses an in-process Development test host and requires no database or separately started server. It never starts a public endpoint.

```powershell
Set-Location D:\TaskFlow
npm ci --prefix tools/api-contracts
$env:TASKFLOW_OPENAPI_EXPORT = Join-Path (Get-Location) 'docs/contracts/openapi.json'
try {
    dotnet test TaskFlow.slnx --configuration Release --artifacts-path .local/build/contracts --filter FullyQualifiedName~SnapshotMatchesLiveContract /p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Contract export failed.' }
} finally {
    Remove-Item Env:TASKFLOW_OPENAPI_EXPORT -ErrorAction SilentlyContinue
}
npm run generate --prefix tools/api-contracts
git diff -- docs/contracts/openapi.json TaskFlow-FE/src/api/generated.d.ts
```

The export variable explicitly opts out of snapshot comparison for regeneration only. Never set it in CI. Review controller/DTO/transformer changes together with both generated files; do not edit generated output to hide drift. openapi-typescript 7.13.0 requires TypeScript 5, so its locked tooling package uses 5.9.3 independently of the frontend's TypeScript 6.0.3. No forced peer-dependency override is used. Generation disables default-non-nullable so a server default does not falsely make an optional input required.

## Verify without rewriting

```powershell
dotnet test TaskFlow.slnx --configuration Release --artifacts-path .local/build/contracts --filter FullyQualifiedName~OpenApiContractTests /p:RestoreLockedMode=true
npm run check --prefix tools/api-contracts
npm run typecheck --prefix TaskFlow-FE
```

Four backend contract tests compare the complete live document with the checked-in snapshot (object key order ignored), verify 71 uniquely named operations and security/error metadata, and check key schema invariants. Semantic route/type/status/query/security changes fail. Frontend CI installs the isolated locked tool and checks generated output before typechecking consumers. Compile-time assertions reject numeric IDs, changed status unions and incorrect PATCH nullability. Backend CI runs contract tests alongside mandatory live-MySQL tests. Intentional negative probes confirmed that the actual snapshot test rejects an ID changed to integer and the generator's --check rejects stale output; see [verification evidence](../evidence/saas003-contracts.json).

For a freshly rebuilt Development server, the optional HTTP exporter still works and supports a read-only drift check:

```powershell
python scripts/export_openapi.py --url http://localhost:5140/openapi/v1.json --check
```

Only loopback HTTP or verified HTTPS without URL credentials is accepted. Use --ca with a public PEM for private HTTPS; TLS validation stays enabled. The exporter normalizes only servers to the same-origin root. Full release CI and host enforcement remain governed by [release gates](../ci-release-gates.md).

SaaS-009 adds nine read-only v2 operations under an explicit default-off rollout flag; see [workspace reads](../saas/workspace-reads.md). WorkspaceDto has opaque string IDs and Owner/Admin/Member/Guest caller roles. The snapshot includes 404 for disabled/inaccessible preview reads and documents their shared session contract. New project roles, expectedVersion and coordinated v1 retirement remain future work; their presence is not implied by these read-only types.

SaaS-010 adds nine separately gated v2 writes with the existing session/CSRF and presence-aware PATCH contracts. Scope/owner/parent IDs are never writable body properties. Both preview flags default off; no expectedVersion or coordinated v1 retirement is implied. See [scoped write semantics](../saas/workspace-writes.md).

SaaS-011 adds atomic company creation and current-company metadata reads, with existing session/CSRF and preview flags. Only name is writable for creation; see [company navigation](../saas/company-navigation.md).

SaaS-012 adds eight lifecycle operations, explicit project roles, Removed membership state and governance audit DTOs. All three backend flags are required; lifecycle mode closes legacy resource routes. See [lifecycle semantics](../saas/workspace-lifecycle.md).

SaaS-013 adds the versioned `audit` metadata object to activity and governance responses without new public routes (then 36 paths / 52 operations). Private value changes are redacted; version 0 is explicitly legacy. Security records have no browser DTO/route. See [structured audit](../saas/structured-audit.md).

SaaS-014 adds three gated audit console operations: filtered reads, protected export ticket creation and access-rechecked bounded JSON download. Existing readers remain compatible; see [console semantics](../saas/audit-console.md). That snapshot had 39 paths / 55 operations.

SaaS-015 adds decimal-string version properties, X-Expected-Version mutation preconditions and optional Problem Details code. Scoped edits/status/deletion require the loaded revision (428 missing, 400 invalid, 409 resource.conflict stale), checked after authorization; v1 supports opt-in revisions during cutover. No new routes at that stage: 39 paths / 55 operations. See [revision protocol](../saas/resource-versions.md).

SaaS-016 adds two policy/capacity operations, safe quota.exceeded conflicts and current auditExportsEnabled in console responses. Owner/Admin read aggregates; Owner writes require proof and expectedVersion. Quotas are server-controlled and retention settings do not schedule purge. Current inventory: 40 paths / 57 operations. See [pilot policy](../saas/pilot-policy.md).

SaaS-017 adds no HTTP operations or frontend DTOs (40 paths / 57 operations). Transactional event/delivery/attempt tables and a separate checkpoint worker are internal contracts; see [outbox operations](../saas/outbox-workers.md) and [worker telemetry](worker-telemetry.json). Historical checkpoint facts are not recipient-bound send authority.

SaaS-019 adds eight global account-security operations (48 paths / 65 operations): capabilities, self security state, generic verification/recovery requests, protected-token confirmation/reset, current-password change and session-bound reauthentication. Email preview is default off, Development/Staging only; disabled mail routes return 503. Empty 202 does not disclose account existence or guarantee delivery. Tokens/passwords are write-only body fields. See [account security](../saas/account-security.md).

SaaS-020 adds six invitation operations on five paths (53 paths / 71 operations), guarded by the three workspace flags and Invitations:Enabled. Recipient preview/accept require a current verified intended account and a body token, without prior company membership. Managers receive only invitation metadata; raw tokens never appear in responses. See [invitation semantics](../saas/invitations.md).

SaaS-021 adds `InvitationDto.resendAvailableAtUtc` and `PilotCapacityDto.reservedMemberSeats` for accurate cooldown and reserved-capacity presentation. API and frontend must ship together. No routes were added: 53 paths / 71 operations.

SaaS-022 adds five self-only MFA routes, optional login `code`/`recoveryCode`, factor-protected reset, and optional authenticator `code` on change-password/reauthenticate. MFA DTOs expose only self status and deliberately one-time setup/recovery responses. See [MFA rollout](../saas/mfa.md). Review these sensitive response types together with no-store/logging and generated-client changes.
