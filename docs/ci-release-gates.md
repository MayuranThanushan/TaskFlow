# SaaS-002: delivery and security verification

Date: 25 October 2026. Status: implemented locally; hosted/container and repository enforcement evidence pending. See [workflow](../.github/workflows/ci.yml) and [roadmap](../Documents/Feature-Release-Roadmap.md). The GitHub remote is configured; this change does not push commits, change repository settings or publish a release.

Local evidence: [SaaS-002 verification record](evidence/saas002-ci-baseline.json). Passed: 122 live-MySQL backend tests with no skips, 95 frontend tests, 21 operations tests, four real-browser suites and 30 accessibility scans with no detected violations/overflow. Fixed sidebar accessible-name mismatch and form/inline-status boundary contrast uncovered by the published review. These results do not replace hosted checks.

## Required checks

| Check | Gate and evidence |
| --- | --- |
| Backend checks | Locked restore/build, model snapshot drift, live OpenAPI snapshot comparison, operations tests, live disposable MySQL integration tests; TRX verifier rejects empty/failed/skipped tests; TRX and coverage artifact |
| Frontend checks | Locked install, generated API type drift check, typecheck, lint, component tests with JUnit, production build |
| Deployment smoke | Build actual app/migration images; private disposable MySQL; deliberate migration; HTTPS readiness and shell/authentication smoke; app recreation proves persisted keys/session and logout revocation |
| Published browser journeys | Run after container restart smoke, against the same real HTTPS app/database; all business mutations use the actual API; one read-only 500 response is deliberately injected to verify error recovery. Prepare large project/task IDs; owner/member/outsider CRUD, assignment/removal, dashboard/activity, authentication/profile, accessibility/keyboard/security/performance checks; screenshots and JSON artifacts |
| Dependency and secret checks | Audit all five .NET projects and frontend/browser/contract-tool direct/transitive packages; fail high/critical findings, unavailable sources and malformed reports. Gitleaks scans full fetched Git history with pinned, SHA-256-verified CLI and redacted findings |
| Static analysis | Pinned CodeQL action with security-extended analysis for C#, JavaScript/TypeScript and Python. C# uses the pinned SDK, locked restore and a traced manual build; interpreted languages use build mode none. SARIF validation rejects missing/unsuccessful analysis and security severity >=7. SARIF retained and uploaded to code scanning |
| Release checks | Aggregate requires every preceding job to succeed; cancellation, failure or skipped work never closes the gate |

CI runs on main pushes, pull requests, manual dispatch and a weekly schedule so newly disclosed vulnerabilities can fail an unchanged source revision. Disposable passwords in workflow are test-only. No production credentials are passed into PR jobs. Actions use immutable commit pins and checkout does not persist credentials. Only CodeQL receives security-events write permission. No pull_request_target execution of untrusted code is introduced.

## Local commands

Use installed .NET/Node/npm versions from the [toolchain guide](toolchain.md). The dependency audits require registry network access; unavailable audit sources are failures, not clean scans.

```powershell
python -m unittest discover -s tests/operations -v
python scripts/security_audit.py
npm ci --prefix tests/browser
```

Follow [published browser setup](../tests/browser/README.md) for a fresh, isolated HTTPS app and disposable database. Never run those mutation journeys against shared company or development data. The synthetic workspace UI review remains a separate visual check and cannot substitute for real browser/backend verification.

## Software bills of materials and provenance policy

Generate CycloneDX JSON for frontend, browser tools, contract tools and all backend solution dependencies with pinned npm and CycloneDX 6.3.1. Include development/test dependencies and label browser and contract-generator BOMs as tooling. Record source revision and SHA-256 checksums alongside the BOMs. These are dependency inventories, not proof that every component ships in a customer artifact; container OS/runtime inventories are required when assembling a releasable image.

Retain CI security/SBOM artifacts for 14 days and browser/backend/frontend reports for 7 days. These are short-lived CI evidence, not customer audit retention. Do not upload cookies, mounted secrets, connection files, private certificates, production logs or user content. Synthetic screenshots and redacted scanner reports are permitted.

Before a customer release: bind BOMs, container digest/OS inventory, application payload hashes, migration set and all successful check URLs to the exact source revision and immutable release receipt. Generate signed build provenance in a protected release workflow with an approved identity/attestation mechanism; verify it before deployment. PR checksums and CI artifacts are not signed provenance. No signed release or supply-chain certification is claimed by this task. Do not reuse an old passing report for a changed payload.

## Repository setup and external gates

- [ ] Push/review this change through the configured repository and capture a successful run of every required job, including CodeQL and real container browser journeys. Local Windows verification is supplementary.
- [ ] Ensure GitHub Actions and code scanning are enabled for the repository/account. Private repository CodeQL availability depends on account entitlements; resolve this or select an equivalent scanner before declaring the gate complete. Do not silently skip it or purchase services automatically.
- [ ] Protect main with the exact `Release checks` status; require review and prevent ordinary bypass. Configure code-scanning alert protection for high/critical security findings where available. A workflow file alone does not enforce merge protection.
- [ ] Confirm scheduled runs, Dependabot alerts/updates and artifact access/retention policies operate in the repository. Assign a maintainer to triage failures.
- [ ] Attach run URLs, exact revision and artifact identities to the release record. Docker is absent on the current local machine; no hosted/container pass is inferred from workflow lint or a published Windows app.

High/critical findings block merge/release; lower-severity findings require triage before customer release. Any accepted-risk exception must record advisory/rule ID, affected versions, applicability, owner, expiry and compensating control in a reviewed change. No blanket scanner suppression or continue-on-error is allowed. No license compatibility, penetration-test or enterprise compliance approval is implied by a vulnerability scan.

Upstream references: [CodeQL workflow options](https://docs.github.com/en/code-security/reference/code-scanning/workflow-configuration-options), [Gitleaks CLI](https://github.com/gitleaks/gitleaks), [CycloneDX .NET tool](https://github.com/CycloneDX/cyclonedx-dotnet), [npm audit](https://docs.npmjs.com/cli/v11/commands/npm-audit), [npm SBOM](https://docs.npmjs.com/cli/v11/commands/npm-sbom). SaaS-003 typed contracts can proceed while hosted verification remains open; SaaS-005 selected-host release verification remains separate.

## 26 October CI repair

Investigated [main run 40340119870](https://github.com/MayuranThanushan/TaskFlow/actions/runs/40340119870) at revision `0970d024ffd2bd34cc9c4f099b151e4d73e88dfb`. All three analyze steps succeeded; our next SARIF validation step failed with `Analysis is missing rules/results metadata`. Current CodeQL puts query-pack descriptors in `tool.extensions`, leaving `tool.driver.rules` empty. The validator now resolves extension-qualified descriptors and verifies their IDs/indexes. It still rejects incomplete reports, unsuccessful analysis, unresolved descriptors, invalid severities and security findings >=7. Regression cases cover clean extension reports and high-severity findings in them.

Downloaded reports: JavaScript/TypeScript 103 rules, zero findings; Python 50 rules, zero findings; C# 63 rules, three findings. The corrected validator passes the two clean original reports and deliberately rejects the original C# report: two `cs/web/missing-function-level-access-control` findings, severity 7.5, on project/task deletion. All six private resource controllers now declare `[Authorize]` explicitly, in addition to the existing global filter and service-level project/owner checks. Existing anonymous denial and owner/member/outsider integration tests passed. No authorization suppression was added. A fresh hosted C# scan must confirm the findings are resolved.

C# build-free extraction also logged failed dependency restores; scanning now uses an explicit locked restore/build so restoration cannot silently degrade analysis. The original medium `cs/log-forging` finding in request logging remains for triage before customer release; this change does not suppress it or claim a clean fresh scan.

All five artifact action declarations (seven jobs with the matrix) now pin [upload-artifact v7.0.2](https://github.com/actions/upload-artifact/releases/tag/v7.0.2), commit `cf430e030ddbb5b0abf93d22962f4752f3646cd9`. Its action metadata declares Node 24, removing the reported Node 20 dependency. Release checks continue to fail if any required job fails or is skipped. Push the changes and require a fresh successful full workflow before merging/releasing; re-running an old revision cannot validate this fix.
