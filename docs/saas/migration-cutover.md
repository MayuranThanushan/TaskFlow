# Legacy data mapping and v2 cutover

Design contract for SaaS-008-016; no database has been changed by SaaS-001. See [ADR 0005](../decisions/0005-commercial-saas-boundaries.md).

## Deterministic mapping

| Legacy source | Target | Access invariant |
| --- | --- | --- |
| Distinct Projects.OwnerId, including retained deleted projects | One generated legacy workspace per owner | Label as legacy workspace, not a verified legal company |
| Each project, active or deleted | Its owner's mapped workspace | Preserve project ID and authoritative owner |
| Owner | Active workspace Owner; project Owner | No duplicate non-owner project membership |
| Existing non-owner project member | Active workspace Guest and project Editor | Preserve all existing task editing; no additional project visibility |
| Tasks, assignments and activity | Parent project's workspace | Preserve IDs, deletion state and historical actor/assignee references |
| Account owning no projects and collaborating nowhere | Global account only | No automatically invented workspace or company access |
| Same user collaborating with multiple owners | Separate memberships in each mapped workspace | Never merge companies by shared collaborators/email domain |

A collaborator who is an Owner in another mapped workspace remains a Guest in this workspace. Include deleted-only projects when generating owner mappings. Historical actors need not become active members just because an event references them. Retained deleted-task assignees remain historical; active assignees must remain eligible. Detect missing owners/parents, duplicate memberships and unresolved active assignments and stop for explicit reconciliation; do not silently drop rows or grant broader rights.

Persist generated workspace IDs and owner/project mapping in a restricted migration manifest so retries reuse the same mapping. Unique constraints make reruns safe. Record row counts, source snapshot identifier, mapping checksum and access-set comparison, excluding private task content from reports. One owner can represent several real companies today: grouping or splitting requires a later explicit reviewed transfer, never a guess from names or email domains.

## Rollout checklist

- [ ] Inventory full schema, active/deleted counts, ownership/membership/assignment invariants and all tenant entry points; produce a dry-run mapping without modifying the source.
- [ ] Restore a backup into a disposable restricted database and verify restoration before rehearsal.
- [ ] Implement reviewed expand/backfill/enforce phases: nullable tenant columns, persisted deterministic mapping, backfill parent/child data, validate all rows, add tenant-first indexes/composite FKs, then require non-null scope. Use a controlled migration identity, not runtime DDL privileges.
- [ ] Compare pre/post authorized record sets for each user and project, including deleted history. Existing project collaborators retain editing; no new project grants appear. Validate negative cases in the [threat model](tenant-threat-model.md).
- [ ] Rehearse interrupted/backfill retry, constraint failure and transaction rollback. MySQL DDL is not generally rollback-safe; retain phase checkpoints and a restore/forward-fix plan rather than promising transactional schema rollback.
- [ ] Schedule maintenance; stop new writes and workers, drain in-flight work, record final recovery snapshot and migration manifest, then migrate. Do not accept writes during the mapping snapshot/cutover gap.
- [ ] Deploy matching API and UI, enforce v2 scope and retire v1 tenant routes with uniform authenticated 410. Keep global identity routes working. Invalidate old resource caches; verify no stale static frontend uses the retired contract.
- [ ] Verify exact migration readiness, tenant smoke tests, legacy URL handling, counts and denied mixed-company reads/writes before reopening. Record deployed revision, manifests and operator outcome.
- [ ] If verification fails, keep maintenance active and forward-fix or restore the coordinated DB/application/key snapshot according to the rehearsed runbook. Document any recovery data-loss window; never restore a pre-cutover snapshot after new accepted writes without a deliberate reconciliation decision.

Current readiness requires exact compiled/applied migration equality. Old binaries may become not-ready after expansion, so this pilot uses coordinated deployment rather than a rolling or zero-downtime promise. Restore must preserve Identity/data-protection material required for supported encrypted data; global sessions can be explicitly revoked if recovery requires it.

## Future write and reference rules

WorkspaceId is server-derived from authorized route context, never accepted as an arbitrary writable entity property. Task/project, member/project and event/entity relationships must share workspace scope. A global User foreign key is not membership authorization. Keep parent-project locking and recheck assignment/removal eligibility inside each transaction; define a consistent workspace-membership/project lock order before implementing suspension races. Query filters, raw SQL and IgnoreQueryFilters require explicit review.

SaaS-003 defines typed route/DTO/error contracts; SaaS-008 implements mapping; SaaS-009/010 enforce read/write boundaries; SaaS-011/012 implement switching/lifecycle; SaaS-016 supplies the release acceptance report. No customer rollout is complete solely because a migration succeeds.
