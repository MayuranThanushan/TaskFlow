import { useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { useProjectResource } from '../projects/use-project-resource'
export interface ActivityItem { id: string; projectId: string; actorUserId: string; taskId: string | null; actionType: string; details: string; createdAtUtc: string }
export interface ActivityResult { items: ActivityItem[]; page: number; pageSize: number; totalCount: number; totalPages: number }
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
  const { result, retry } = useProjectResource<ActivityResult>(client, '/projects/' + encodeURIComponent(projectId) + '/activity' + (query ? '?' + query : ''))
  function page(value: number) { const next = new URLSearchParams(params); next.set('page', String(value)); setParams(next) }
  return <section className="projects-page"><Link className="text-link" to={'/projects/' + encodeURIComponent(projectId)}>Back to project</Link>
    <div className="page-heading"><div><p className="eyebrow">Project history</p><h1>Project activity</h1><p className="muted">Most recent events first. Times are shown in your local time zone.</p></div><button className="button subtle" disabled={result.status === 'loading'} onClick={retry}>Refresh activity</button></div>
    <ActivityFilters key={query} params={params} apply={setParams} /><p className="field-hint">Date limits include both endpoints. Your local time ({Intl.DateTimeFormat().resolvedOptions().timeZone}) is converted to UTC.</p>
    {result.status === 'loading' && <p role="status" className="project-state">Loading activity...</p>}
    {result.status === 'error' && (result.error.status === 400 ? <div role="alert" className="error-summary"><p>These activity filters are invalid. Choose a valid date range or reset the filters.</p><button className="button subtle" onClick={() => setParams(new URLSearchParams())}>Reset invalid filters</button></div> : result.error.status === 404 ? <div className="project-state"><h2>Project not available.</h2><p role="alert">It may have been removed, or you may not have access.</p></div> : <div role="alert" className="error-summary"><p>{result.error.message}</p><button className="button subtle" onClick={retry}>Try again</button></div>)}
    {result.status === 'ready' && <><p role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'event' : 'events'}</p>
      {result.data.items.length === 0 ? <div className="empty-workspace"><h2>{result.data.totalCount > 0 ? 'No activity on this page.' : params.get('fromUtc') || params.get('toUtc') ? 'No activity in this range.' : 'No activity yet.'}</h2><p>Activity appears here as your project changes.</p>{result.data.totalCount > 0 && <button className="button subtle" onClick={() => page(1)}>Go to first page</button>}</div>
        : <ol className="activity-list">{result.data.items.map(event => <li key={event.id}><h2>{event.details}</h2><p>User ID: {event.actorUserId}{event.taskId && <span> | Task ID: {event.taskId}</span>}</p><time dateTime={event.createdAtUtc}>{new Date(event.createdAtUtc).toLocaleString()}</time></li>)}</ol>}
      {result.data.totalPages > 1 && <nav className="pagination" aria-label="Activity pages"><button className="button subtle" disabled={result.data.page <= 1} onClick={() => page(result.data.page - 1)}>Previous</button><span>Page {result.data.page} of {result.data.totalPages}</span><button className="button subtle" disabled={result.data.page >= result.data.totalPages} onClick={() => page(result.data.page + 1)}>Next</button></nav>}
    </>}
  </section>
}
