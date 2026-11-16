import { useState } from 'react'
import type { FormEvent } from 'react'
import type { ApiClient } from '../../api/client'
import { Badge } from '../../components/Workspace'
import { Pagination } from '../../components/Feedback'
import { useApiResource } from '../../hooks/use-api-resource'
import { useAuth } from '../auth/auth-context'
import { Link, useSearchParams } from '../companies/navigation'
import { queues, workPath } from './work-query'
import type { WorkPage as WorkResult } from './work-query'
import './work.css'

const statusText = { ToDo: 'To do', InProgress: 'In progress', Done: 'Completed' }
function Filters({ params, apply }: { params: URLSearchParams; apply: (p: URLSearchParams) => void }) {
  const [search, setSearch] = useState(params.get('search') ?? ''), [assignment, setAssignment] = useState(params.get('assignment') ?? 'mine')
  const [status, setStatus] = useState(params.get('status') ?? ''), [priority, setPriority] = useState(params.get('priority') ?? '')
  const [from, setFrom] = useState(params.get('fromDate') ?? ''), [through, setThrough] = useState(params.get('throughDate') ?? '')
  const [sort, setSort] = useState(params.get('sortBy') ?? 'dueDateUtc'), [direction, setDirection] = useState(params.get('sortDirection') ?? 'asc'), [size, setSize] = useState(params.get('pageSize') ?? '20')
  const [rawDates, setRawDates] = useState(params.has('fromUtc') || params.has('toUtc'))
  function submit(event: FormEvent) {
    event.preventDefault()
    const next = new URLSearchParams({ assignment, queue: params.get('queue') ?? 'open', pageSize: size, sortBy: sort, sortDirection: direction })
    if (search.trim()) next.set('search', search.trim())
    if (status) next.set('status', status)
    if (priority) next.set('priority', priority)
    if (from) next.set('fromDate', from)
    if (through) next.set('throughDate', through)
    if (rawDates && !from && !through) for (const key of ['fromUtc', 'toUtc']) if (params.has(key)) next.set(key, params.get(key)!)
    if (params.has('projectId')) next.set('projectId', params.get('projectId')!)
    apply(next)
  }
  return <form className="work-filters" aria-label="Find work" onSubmit={submit}>
    <div className="work-search"><label htmlFor="work-search">Search tasks or projects</label><input id="work-search" type="search" value={search} maxLength={200} placeholder="Task title or project name" onChange={e => setSearch(e.target.value)} /></div>
    <div><label htmlFor="work-assignment">Show work</label><select id="work-assignment" value={assignment} onChange={e => setAssignment(e.target.value)}><option value="mine">Assigned to me</option><option value="unassigned">Unassigned</option><option value="all">All visible work</option></select></div>
    <div><label htmlFor="work-status">Status</label><select id="work-status" value={status} onChange={e => setStatus(e.target.value)}><option value="">All statuses</option>{Object.entries(statusText).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select></div>
    <div><label htmlFor="work-priority">Priority</label><select id="work-priority" value={priority} onChange={e => setPriority(e.target.value)}><option value="">All priorities</option>{['Low', 'Medium', 'High'].map(value => <option key={value}>{value}</option>)}</select></div>
    <div><label htmlFor="work-from">Deadline from date</label><input id="work-from" type="date" value={from} onChange={e => setFrom(e.target.value)} /></div>
    <div><label htmlFor="work-through">Deadline through date</label><input id="work-through" type="date" value={through} min={from || undefined} onChange={e => setThrough(e.target.value)} /></div>
    <div><label htmlFor="work-sort">Sort by</label><select id="work-sort" value={sort} onChange={e => setSort(e.target.value)}><option value="dueDateUtc">Deadline</option><option value="createdAtUtc">Created date</option><option value="title">Task title</option></select></div>
    <div><label htmlFor="work-direction">Order</label><select id="work-direction" value={direction} onChange={e => setDirection(e.target.value)}><option value="asc">Ascending</option><option value="desc">Descending</option></select></div>
    <div><label htmlFor="work-page-size">Per page</label><select id="work-page-size" value={size} onChange={e => setSize(e.target.value)}>{[20, 50, 100].map(value => <option key={value}>{value}</option>)}</select></div>
    <div className="form-actions"><button className="button primary">Apply filters</button><button className="button subtle" type="button" onClick={() => apply(new URLSearchParams())}>Reset filters</button><button type="button" className="text-button" onClick={() => { setFrom(''); setThrough(''); setRawDates(false) }}>Clear date fields</button></div>
    <p className="field-hint work-filter-help">Dates include the whole selected day in your device timezone ({Intl.DateTimeFormat().resolvedOptions().timeZone}). Unassigned tasks are a separate choice from Assigned to me.</p>
    {rawDates && !from && !through && <p className="field-hint work-filter-help">A custom timestamp range from this link is active. Clear date fields and apply to remove it.</p>}
    {params.has('projectId') && <p className="field-hint work-filter-help">This link is restricted to one project. Reset filters to search all projects you can access.</p>}
  </form>
}
export function WorkPage({ client }: { client: ApiClient }) {
  const { session } = useAuth(), [params, setParams] = useSearchParams(), query = params.toString()
  const { result, retry } = useApiResource<WorkResult>(client, workPath(params))
  const queue = params.get('queue') ?? 'open'
  function chooseQueue(value: string) { const next = new URLSearchParams(params); next.set('queue', value); next.delete('page'); setParams(next) }
  function page(value: number) { const next = new URLSearchParams(params); next.set('page', String(value)); setParams(next) }
  return <section className="projects-page work-page">
    <div className="page-heading"><div><p className="eyebrow">Across your projects</p><h1>My work</h1><p className="muted">Find work in this company, across projects you can access.</p></div><div className="form-actions"><Link className="button subtle" to="/projects">Browse projects</Link><button className="button subtle" disabled={result.status === 'loading'} onClick={retry}>Refresh work</button></div></div>
    <Filters key={query} params={params} apply={setParams} />
    <nav className="work-queues" aria-label="Work queues">{queues.map(q => <button type="button" key={q.value} className={'button ' + (queue === q.value ? 'primary' : 'subtle')} aria-pressed={queue === q.value} onClick={() => chooseQueue(q.value)}>{q.label}{result.status === 'ready' && <span className="work-queue-count">{result.data.counts[q.count]}</span>}</button>)}</nav>
    <p className="field-hint">Due soon means unfinished work due within the next seven days. Overdue excludes completed work. Tasks without a deadline stay in Open work.</p>
    {result.status === 'loading' && <p role="status">Loading work...</p>}
    {result.status === 'error' && <div role="alert" className="error-summary"><h2>{result.error.status === 404 ? 'Company or project not available.' : result.error.status === 400 ? 'These work filters are invalid.' : 'Work could not be loaded.'}</h2><p>{result.error.status === 404 ? 'Your access may have changed. Choose a company or return to projects.' : result.error.message}</p>{result.error.status === 400 ? <button className="button subtle" onClick={() => setParams(new URLSearchParams())}>Reset invalid filters</button> : <button className="button subtle" onClick={retry}>Try again</button>}</div>}
    {result.status === 'ready' && <>
      <div className="work-result-meta"><p role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'task' : 'tasks'} found</p><p className="field-hint">Updated <time dateTime={result.data.asOfUtc}>{new Date(result.data.asOfUtc).toLocaleString()}</time>. Due soon through {new Date(result.data.dueThroughUtc).toLocaleString()}.</p></div>
      {result.data.items.length === 0 ? <div className="empty-workspace"><h2>{result.data.totalCount ? 'No work on this page.' : 'No work matches this view.'}</h2><p>{result.data.totalCount ? 'Return to the first page to see current work.' : 'Try another queue or assignment choice, adjust your search, or browse your projects. Project owners and editors can assign unassigned work.'}</p>{result.data.totalCount > 0 && <button className="button subtle" onClick={() => page(1)}>Go to first work page</button>}</div> : <ul className="work-results" aria-label="Work results">{result.data.items.map(({ task, projectName, assigneeName }) => {
        const overdue = task.status !== 'Done' && !!task.dueDateUtc && new Date(task.dueDateUtc).getTime() < new Date(result.data.asOfUtc).getTime()
        return <li key={task.id} className="work-row"><div className="work-row-title"><Link className="work-project-link" to={'/projects/' + task.projectId}>{projectName}</Link><h2><Link to={'/tasks/' + task.id}>{task.title}</Link></h2><span className="muted">{task.assigneeId === session.user?.id ? 'Assigned to you' : assigneeName ?? 'Unassigned'}</span></div><div className="work-badges"><Badge value={task.status}>{statusText[task.status]}</Badge><Badge value={task.priority} /></div><p className={overdue ? 'work-deadline work-overdue' : 'work-deadline'}>{overdue && <strong>Overdue: </strong>}{task.dueDateUtc ? <time dateTime={task.dueDateUtc}>{new Date(task.dueDateUtc).toLocaleString()}</time> : 'No deadline'}</p><Link className="text-link work-board-link" to={'/projects/' + task.projectId + '/board'} aria-label={'Open board for ' + projectName}>Open board</Link></li>
      })}</ul>}
      <Pagination label="Work pages" page={result.data.page} totalPages={result.data.totalPages} onPage={page} />
    </>}
  </section>
}
