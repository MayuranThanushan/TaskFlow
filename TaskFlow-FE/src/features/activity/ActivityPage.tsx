import type { components } from '../../api/generated'
import { Badge } from '../../components/Workspace'
import { useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { useProjectResource } from '../projects/use-project-resource'
export type ActivityItem = components['schemas']['ActivityDto']
export type ActivityResult = components['schemas']['ActivityPage']
function localTime(value: string | null) { if (!value) return ''; const date = new Date(value); return Number.isNaN(date.getTime()) ? '' : new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 23) }
function ActivityFilters({ params, apply }: { params: URLSearchParams; apply: (next: URLSearchParams) => void }) {
  const [from, setFrom] = useState(localTime(params.get('fromUtc'))); const [to, setTo] = useState(localTime(params.get('toUtc')))
  const [size, setSize] = useState(params.get('pageSize') ?? '20'); const [error, setError] = useState('')
  function instant(value: string, key: string) { const original = params.get(key); return original && !Number.isNaN(new Date(original).getTime()) && value === localTime(original) ? original : value ? new Date(value).toISOString() : null }
  return <form className="project-filters" aria-label="Filter activity" onSubmit={event => {
    event.preventDefault(); setError('')
    if ([from, to].some(value => value && Number.isNaN(new Date(value).getTime()))) { setError('Enter valid local dates and times.'); return }
    const lower = instant(from, 'fromUtc'); const upper = instant(to, 'toUtc')
    if (lower && upper && new Date(lower) > new Date(upper)) { setError('The start must not be later than the end.'); return }
    const next = new URLSearchParams({ pageSize: size }); if (lower) next.set('fromUtc', lower); if (upper) next.set('toUtc', upper); apply(next)
  }}>
    <div><label htmlFor="activity-from">From date and time (optional)</label><input id="activity-from" type="datetime-local" step="0.001" value={from} onChange={event => setFrom(event.target.value)} /></div>
    <div><label htmlFor="activity-to">To date and time (optional)</label><input id="activity-to" type="datetime-local" step="0.001" value={to} onChange={event => setTo(event.target.value)} /></div>
    <div><label htmlFor="activity-size">Per page</label><select id="activity-size" value={size} onChange={event => setSize(event.target.value)}>{[10,20,50,100].map(value => <option key={value}>{value}</option>)}</select></div>
    <button className="button subtle" type="submit">Apply</button><button className="button subtle" type="button" onClick={() => apply(new URLSearchParams())}>Reset</button>
    {error && <p role="alert" className="field-error">{error}</p>}
  </form>
}
export function ActivityPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams(); const [params, setParams] = useSearchParams(); const query = params.toString()
  const [exportError, setExportError] = useState('')
  const members = useProjectResource<{ items: { userId: string; displayName: string }[] }>(client, '/projects/' + encodeURIComponent(projectId) + '/members')
  const { result, retry } = useProjectResource<ActivityResult>(client, '/projects/' + encodeURIComponent(projectId) + '/activity' + (query ? '?' + query : ''))
  function exportPage() {
    if (result.status !== 'ready') return
    setExportError('')
    try {
      const content = { projectId, exportedAtUtc: new Date().toISOString(), scope: 'Current filtered page only', filters: Object.fromEntries(params), page: result.data.page, totalCount: result.data.totalCount, events: result.data.items }
      const url = URL.createObjectURL(new Blob([JSON.stringify(content, null, 2)], { type: 'application/json' }))
      const link = document.createElement('a'); link.href = url; link.download = `taskflow-activity-${projectId}-page-${result.data.page}.json`; link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch { setExportError('Export is unavailable in this browser. Please try again.') }
  }
  function page(value: number) { const next = new URLSearchParams(params); next.set('page', String(value)); setParams(next) }
  return <section className="projects-page"><Link className="text-link" to={'/projects/' + encodeURIComponent(projectId)}>Back to project</Link>
    <div className="page-heading"><div><p className="eyebrow">Project history</p><h1>Project activity</h1><p className="muted">An immutable timeline of project, task and team changes. Most recent events first.</p></div><div className="form-actions"><button className="button subtle" disabled={result.status !== 'ready' || result.data.items.length === 0} onClick={exportPage}>Export this page</button><button className="button subtle" disabled={result.status === 'loading'} onClick={retry}>Refresh activity</button></div></div>
    {exportError && <p role="alert" className="error-summary">{exportError}</p>}
    <ActivityFilters key={query} params={params} apply={setParams} /><p className="field-hint">Date limits include both endpoints. Your local time ({Intl.DateTimeFormat().resolvedOptions().timeZone}) is converted to UTC.</p>
    {result.status === 'loading' && <p role="status" className="project-state">Loading activity...</p>}
    {result.status === 'error' && (result.error.status === 400 ? <div role="alert" className="error-summary"><p>These activity filters are invalid. Choose a valid date range or reset the filters.</p><button className="button subtle" onClick={() => setParams(new URLSearchParams())}>Reset invalid filters</button></div> : result.error.status === 404 ? <div className="project-state"><h2>Project not available.</h2><p role="alert">It may have been removed, or you may not have access.</p></div> : <div role="alert" className="error-summary"><p>{result.error.message}</p><button className="button subtle" onClick={retry}>Try again</button></div>)}
    {result.status === 'ready' && <><p role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'event' : 'events'}</p>
      {result.data.items.length === 0 ? <div className="empty-workspace"><h2>{result.data.totalCount > 0 ? 'No activity on this page.' : params.get('fromUtc') || params.get('toUtc') ? 'No activity in this range.' : 'No activity yet.'}</h2><p>Activity appears here as your project changes.</p>{result.data.totalCount > 0 && <button className="button subtle" onClick={() => page(1)}>Go to first page</button>}</div>
        : <ol className="activity-list audit-timeline">{result.data.items.map(event => <li key={event.id}><span className="timeline-marker" aria-hidden="true">{event.actionType.includes('Deleted') || event.actionType.includes('Removed') ? '-' : event.actionType.includes('Created') || event.actionType.includes('Added') ? '+' : '\u2197'}</span><div className="audit-event-top"><Badge value={event.actionType.includes('Deleted') || event.actionType.includes('Removed') ? 'removed' : 'event'}>{event.actionType.replace(/([a-z])([A-Z])/g, '$1 $2')}</Badge><span className="audit-record">Event #{event.id}</span></div><h2>{event.details}</h2><p>{members.result.status === 'ready' && members.result.data.items.find(member => member.userId === event.actorUserId)?.displayName && <strong>{members.result.data.items.find(member => member.userId === event.actorUserId)?.displayName} &middot; </strong>}User ID: {event.actorUserId}{event.taskId && <span> | Task ID: {event.taskId}</span>}</p><time dateTime={event.createdAtUtc}>{new Date(event.createdAtUtc).toLocaleString()}</time></li>)}</ol>}
      {result.data.totalPages > 1 && <nav className="pagination" aria-label="Activity pages"><button className="button subtle" disabled={result.data.page <= 1} onClick={() => page(result.data.page - 1)}>Previous</button><span>Page {result.data.page} of {result.data.totalPages}</span><button className="button subtle" disabled={result.data.page >= result.data.totalPages} onClick={() => page(result.data.page + 1)}>Next</button></nav>}
    </>}
  </section>
}
