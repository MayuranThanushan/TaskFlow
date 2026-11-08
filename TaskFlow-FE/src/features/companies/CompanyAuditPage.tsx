import { useEffect, useRef, useState } from 'react'
import { useSearchParams } from 'react-router-dom'
import { asApiError, type ApiClient } from '../../api/client'
import type { components } from '../../api/generated'
import { AuditDetails } from '../../components/AuditDetails'
import { Pagination, RetryError, TableRegion } from '../../components/Feedback'
import { useApiResource } from '../../hooks/use-api-resource'

type Filters = Record<string, string>
const names = ['source', 'actorId', 'action', 'entityType', 'entityId', 'fromUtc', 'toUtc', 'outcome', 'resourceState']
function filters(params: URLSearchParams): Filters {
  return Object.fromEntries(names.map(name => [name, params.get(name) ?? (name === 'source' ? 'Governance' : name === 'resourceState' ? 'All' : '')]))
}
export function CompanyAuditPage({ client }: { client: ApiClient }) {
  const [params] = useSearchParams()
  return <AuditConsole key={client.resourceKey + ":" + params.toString()} client={client} />
}
function AuditConsole({ client }: { client: ApiClient }) {
  const [params, setParams] = useSearchParams()
  const applied = filters(params)
  const [draft, setDraft] = useState(applied)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const alive = useRef(true)
  const busy = useRef(false)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  const query = new URLSearchParams(Object.entries(applied).filter(([, value]) => value))
  query.set('page', params.get('page') ?? '1')
  const { result, retry } = useApiResource<components['schemas']['AuditConsolePage']>(client, '/audit/console?' + query)
  const change = (name: string, value: string) => setDraft(previous => ({ ...previous, [name]: value, ...(name === 'source' ? { resourceState: 'All' } : {}) }))
  async function download() {
    if (busy.current) return
    busy.current = true; setPending(true); setError(''); setNotice('')
    try {
      const ticket = await client.request<components['schemas']['AuditExportTicketDto']>({ url: '/audit/console/exports', method: 'post', data: { filters: Object.fromEntries(Object.entries(applied).filter(([, value]) => value)) } })
      if (!alive.current) return
      const data = await client.request<components['schemas']['AuditExportDto']>({ url: '/audit/console/exports/download', method: 'post', data: { ticket: ticket.ticket } })
      if (!alive.current) return
      const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }))
      const anchor = document.createElement('a'); anchor.href = url; anchor.download = 'taskflow-audit.json'; anchor.click()
      setTimeout(() => URL.revokeObjectURL(url), 1000)
      setNotice(data.truncated ? 'Downloaded 1,000 records. More records match; narrow the date range to export the rest.' : `Downloaded ${data.items.length} audit records.`)
    } catch (failure) { if (alive.current) { const issue = asApiError(failure); setError([issue.message, ...Object.values(issue.fields).flat()].join(' ')) } }
    finally { busy.current = false; if (alive.current) setPending(false) }
  }
  return <section className="projects-page">
    <div className="page-heading"><div><p className="eyebrow">Governance history</p><h1>Company audit</h1><p className="muted">Review governance and permitted project history, including deleted projects. Private names and content are excluded.</p></div></div>
    <form className="audit-filters" onSubmit={event => { event.preventDefault(); setParams(Object.fromEntries(Object.entries(draft).filter(([, value]) => value))); setNotice(''); setError('') }}>
      <label>Event source<select value={draft.source} onChange={e => change('source', e.target.value)}><option>Governance</option><option value="Projects">Project activity</option></select></label>
      <label>Actor ID<input inputMode="numeric" pattern="[0-9]+" maxLength={19} value={draft.actorId} onChange={e => change('actorId', e.target.value)} /></label>
      <label>Action<input placeholder="e.g. ProjectDeleted" pattern="[A-Za-z]+" maxLength={80} value={draft.action} onChange={e => change('action', e.target.value)} /></label>
      <label>Entity type<select value={draft.entityType} onChange={e => change('entityType', e.target.value)}><option value="">All entity types</option>{['Project', 'Task', 'ProjectMembership', 'Workspace', 'WorkspaceMembership'].map(type => <option key={type}>{type}</option>)}</select></label>
      <label>Entity ID<input pattern="[0-9:]+" maxLength={40} value={draft.entityId} onChange={e => change('entityId', e.target.value)} /><span className="field-hint">Membership IDs use parent:account.</span></label>
      <label>From timestamp<input placeholder="2026-11-01T00:00:00Z" maxLength={40} value={draft.fromUtc} onChange={e => change('fromUtc', e.target.value)} /><span className="field-hint">ISO format with Z or an explicit offset.</span></label>
      <label>To timestamp<input placeholder="2026-11-01T23:59:59Z" maxLength={40} value={draft.toUtc} onChange={e => change('toUtc', e.target.value)} /></label>
      <label>Outcome<select value={draft.outcome} onChange={e => change('outcome', e.target.value)}><option value="">All outcomes</option><option>Succeeded</option><option value="LegacyUnknown">Legacy / unknown</option></select></label>
      {draft.source === 'Projects' && <label>Project state<select value={draft.resourceState} onChange={e => change('resourceState', e.target.value)}><option value="All">All projects</option><option value="Active">Active projects</option><option value="Deleted">Deleted projects</option></select></label>}
      <div className="action-row"><button type="submit" className="button primary" disabled={pending}>Apply filters</button><button type="button" className="button subtle" disabled={pending} onClick={() => { const empty = filters(new URLSearchParams()); setDraft(empty); setParams({}); setNotice(''); setError('') }}>Clear filters</button></div>
    </form>
    <div className="audit-export-bar"><div><strong>Export applied filters</strong><p className="field-hint">JSON, maximum 1,000 records. Access is checked again at download. Security events are excluded.</p></div><button type="button" className="button subtle" disabled={pending || result.status !== 'ready' || result.data.auditExportsEnabled === false} onClick={() => void download()}>{pending ? 'Preparing export?' : 'Download audit JSON'}</button></div>
    {result.status === 'ready' && result.data.auditExportsEnabled === false && <p className="notice">Audit downloads are disabled by company policy. You can still review permitted history.</p>}
    {error && <p role="alert">{error}</p>}{notice && <p role="status">{notice}</p>}
    {result.status === 'loading' ? <p role="status">Loading company audit...</p> : result.status === 'error' ? <RetryError message={[result.error.message, ...Object.values(result.error.fields).flat()].join(' ')} retry={retry} /> : <>
      <p className="field-hint">{result.data.totalCount} matching events. Most recent first. IDs stay exact in exports.</p>
      {result.data.items.length === 0 ? <p>No audit events match these filters.</p> : <TableRegion label="Company audit table"><table className="task-table"><caption className="sr-only">Company audit events</caption><thead><tr><th scope="col">When</th><th scope="col">Action</th><th scope="col">Actor ID</th><th scope="col">Project ID</th><th scope="col">Audit details</th></tr></thead><tbody>{result.data.items.map(event => <tr key={event.id}><td><time dateTime={event.createdAtUtc}>{new Date(event.createdAtUtc).toLocaleString()}</time></td><td>{event.action}{event.projectDeleted && <span className="status-badge">Deleted project</span>}</td><td>{event.actorUserId}</td><td>{event.projectId ?? '\u2014'}</td><td><AuditDetails audit={event.audit} /></td></tr>)}</tbody></table></TableRegion>}
      <Pagination label="Company audit pages" page={result.data.page} totalPages={Math.min(result.data.totalPages, Math.floor(10000 / result.data.pageSize) + 1)} onPage={page => { const next = new URLSearchParams(params); next.set('page', String(page)); setParams(next) }} />
      {result.data.totalCount > 10000 && <p className="field-hint">Narrow your filters to review history beyond the first 10,000 records.</p>}
    </>}
  </section>
}
