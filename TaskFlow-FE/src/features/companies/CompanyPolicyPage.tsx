import { useState } from 'react'
import type { ApiClient } from '../../api/client'
import type { components } from '../../api/generated'
import { useApiResource } from '../../hooks/use-api-resource'
import { RetryError } from '../../components/Feedback'
import { GovernanceAction } from './GovernanceAction'

type Policy = components['schemas']['WorkspacePolicyDto']
export function CompanyPolicyPage({ client, owner }: { client: ApiClient; owner: boolean }) {
  const { result, retry } = useApiResource<Policy>(client, '/policy')
  return <section className="projects-page"><div className="page-heading"><div><p className="eyebrow">Private pilot controls</p><h1>Policy &amp; capacity</h1><p className="muted">Company capacity totals include private projects. Project content and collaborator access stay separate.</p></div></div>
    {result.status === 'loading' ? <p role="status">Loading company policy...</p> : result.status === 'error' ? <RetryError message={result.error.message} retry={retry} /> : <>
      <div className="pilot-capacity" aria-label="Company capacity">{[
        ['Active projects', result.data.capacity.activeProjects, result.data.capacity.projectLimit],
        ['Active tasks', result.data.capacity.activeTasks, result.data.capacity.taskLimit],
        ['Active members', result.data.capacity.activeMembers, result.data.capacity.memberLimit],
        ['Retained projects', result.data.capacity.retainedProjects, result.data.capacity.retainedProjectLimit],
        ['Retained tasks', result.data.capacity.retainedTasks, result.data.capacity.retainedTaskLimit],
      ].map(([label, used, limit]) => <div className={Number(used) >= Number(limit) ? "at-capacity" : undefined} key={label}><span>{label}</span><strong>{used} / {limit}</strong></div>)}</div>
      <p className="field-hint">Each project supports up to {result.data.capacity.tasksPerProjectLimit} active tasks. Deleted projects/tasks remain in retained totals. Limits are managed by operations.</p>
      <p className="notice">Automatic deletion is off. These periods guide retention reviews; they do not schedule or guarantee erasure. A legal hold requires records to remain retained.</p>
      <PolicyForm key={result.data.version} client={client} initial={result.data} owner={owner} refresh={retry} />
      <button className="button subtle" type="button" onClick={retry}>Reload policy and discard draft</button>
    </>}
  </section>
}
function PolicyForm({ client, initial, owner, refresh }: { client: ApiClient; initial: Policy; owner: boolean; refresh: () => void }) {
  const [audit, setAudit] = useState(initial.auditRetentionDays), [content, setContent] = useState(initial.deletedContentRetentionDays)
  const [hold, setHold] = useState(initial.legalHold), [exports, setExports] = useState(initial.auditExportsEnabled)
  if (!owner) return <section><h2>Retention review policy</h2><dl className="task-details"><div><dt>Audit history review</dt><dd>{audit} days</dd></div><div><dt>Deleted content review</dt><dd>{content} days</dd></div><div><dt>Legal hold</dt><dd>{hold ? 'On' : 'Off'}</dd></div><div><dt>Audit downloads</dt><dd>{exports ? 'Enabled' : 'Disabled'}</dd></div></dl><p>Only an active company Owner can change this policy.</p></section>
  return <GovernanceAction client={client} path="/policy" method="put" title="Change company policy" passwordRequired data={{ expectedVersion: initial.version, auditRetentionDays: audit, deletedContentRetentionDays: content, legalHold: hold, auditExportsEnabled: exports }} description="Confirm these retention review and export controls. This change is audited. Turning off exports blocks new downloads, including previously issued tickets. No records are deleted by this action." onSuccess={refresh}>
    <label>Audit history review period<select value={audit} onChange={e => setAudit(Number(e.target.value))}>{[90, 180, 365, 730].map(days => <option value={days} key={days}>{days} days</option>)}</select></label>
    <label>Deleted content review period<select value={content} onChange={e => setContent(Number(e.target.value))}>{[30, 90, 180, 365].map(days => <option value={days} key={days}>{days} days</option>)}</select></label>
    <label className="policy-checkbox"><input type="checkbox" checked={hold} onChange={e => setHold(e.target.checked)} />Legal hold</label>
    <label className="policy-checkbox"><input type="checkbox" checked={exports} onChange={e => setExports(e.target.checked)} />Allow company audit downloads</label>
  </GovernanceAction>
}
