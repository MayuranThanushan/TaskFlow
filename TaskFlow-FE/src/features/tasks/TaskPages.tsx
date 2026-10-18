import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { asApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import type { FieldErrors } from '../../api/types'
import { useProjectResource } from '../projects/use-project-resource'
import type { ProjectDetails } from '../projects/types'
import type { TaskItem, TaskPage } from './types'

const statusText = { ToDo: 'To Do', InProgress: 'In Progress', Done: 'Completed' }
function deadline(value: string | null) { return value ? new Date(value).toLocaleString() : 'No deadline' }
function Failure({ status, message, retry }: { status: number | undefined; message: string; retry: () => void }) {
  return <div className="project-state"><h1>{status === 404 ? 'Task or project not available.' : status === 400 ? 'That task link is invalid.' : 'We could not load this task view.'}</h1><p role="alert">{status === 404 ? 'It may have been removed, or you may not have access.' : message}</p>{status !== 404 && status !== 400 && <button className="button subtle" onClick={retry}>Try again</button>}</div>
}
function TaskQuery({ params, apply }: { params: URLSearchParams; apply: (next: URLSearchParams) => void }) {
  const [search, setSearch] = useState(params.get('search') ?? '')
  const [pageSize, setPageSize] = useState(params.get('pageSize') ?? '20')
  return <form className="project-filters" aria-label="Find tasks" onSubmit={event => { event.preventDefault(); const next = new URLSearchParams(params); next.delete('page'); next.set('pageSize', pageSize); if (search.trim()) next.set('search', search.trim()); else next.delete('search'); apply(next) }}>
    <div><label htmlFor="task-search">Search tasks</label><input id="task-search" type="search" maxLength={200} value={search} onChange={event => setSearch(event.target.value)} /></div>
    <div><label htmlFor="task-page-size">Per page</label><select id="task-page-size" value={pageSize} onChange={event => setPageSize(event.target.value)}>{[10, 20, 50, 100].map(size => <option key={size}>{size}</option>)}</select></div>
    <button className="button subtle">Apply</button><button className="button subtle" type="button" onClick={() => apply(new URLSearchParams())}>Reset</button>
  </form>
}
export function TasksPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams(); const [params, setParams] = useSearchParams(); const query = params.toString()
  const path = '/projects/' + encodeURIComponent(projectId)
  const { result, retry } = useProjectResource<TaskPage>(client, path + '/tasks' + (query ? '?' + query : ''))
  const page = (value: number) => { const next = new URLSearchParams(params); next.set('page', String(value)); setParams(next) }
  return <section className="projects-page"><Link className="text-link" to={path}>Back to project</Link>
    <div className="page-heading"><div><p className="eyebrow">Project work</p><h1>Tasks</h1></div><Link className="button primary" to={path + '/tasks/new'}>New task</Link></div>
    <TaskQuery key={query} params={params} apply={setParams} />
    {result.status === 'loading' && <p role="status">Loading tasks...</p>}
    {result.status === 'error' && <Failure status={result.error.status} message={result.error.message} retry={retry} />}
    {result.status === 'ready' && <><p role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'task' : 'tasks'}</p>
      {result.data.items.length === 0 ? <div className="empty-workspace"><h2>{result.data.totalCount > 0 ? 'No tasks on this page.' : params.get('search') ? 'No tasks match your search.' : 'No tasks yet.'}</h2><p>Create a task or adjust your search to find work here.</p>{result.data.totalCount > 0 && <button className="button subtle" onClick={() => page(1)}>Go to first page</button>}</div>
        : <ul className="project-grid">{result.data.items.map(task => <li key={task.id} className="project-card"><h2><Link to={'/tasks/' + encodeURIComponent(task.id)}>{task.title}</Link></h2><p>{statusText[task.status]} | {task.priority} priority</p><p>{deadline(task.dueDateUtc)}</p><p>Assignee: {task.assigneeId ?? 'Unassigned'}</p></li>)}</ul>}
      {result.data.totalPages > 1 && <nav className="pagination" aria-label="Task pages"><button className="button subtle" disabled={result.data.page <= 1} onClick={() => page(result.data.page - 1)}>Previous</button><span>Page {result.data.page} of {result.data.totalPages}</span><button className="button subtle" disabled={result.data.page >= result.data.totalPages} onClick={() => page(result.data.page + 1)}>Next</button></nav>}
    </>}
  </section>
}
export function NewTaskPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams()
  const { result, retry } = useProjectResource<ProjectDetails>(client, '/projects/' + encodeURIComponent(projectId))
  return result.status === 'loading' ? <p role="status">Loading project...</p> : result.status === 'error' ? <><Link to="/projects">Back to projects</Link><Failure status={result.error.status} message={result.error.message} retry={retry} /></>
    : <TaskForm key={projectId} client={client} project={result.data} />
}
function TaskForm({ client, project }: { client: ApiClient; project: ProjectDetails }) {
  const navigate = useNavigate(); const path = '/projects/' + encodeURIComponent(project.id)
  const members = useProjectResource<{ items: { userId: string; displayName: string; role: string }[] }>(client, path + '/members')
  const [title, setTitle] = useState(''); const [description, setDescription] = useState(''); const [priority, setPriority] = useState('Medium')
  const [assignee, setAssignee] = useState(''); const [due, setDue] = useState(''); const [fields, setFields] = useState<FieldErrors>({})
  const [error, setError] = useState(''); const [pending, setPending] = useState(false)
  const alive = useRef(true); const lock = useRef(false); const summary = useRef<HTMLDivElement>(null)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault(); if (lock.current) return
    const errors: FieldErrors = {}; const parsed = due ? new Date(due) : null
    if (!title.trim() || title.trim().length > 200) errors.title = ['Enter a title between 1 and 200 characters.']
    if (description.trim().length > 10_000) errors.description = ['Keep the description to 10,000 characters or fewer.']
    if (parsed && Number.isNaN(parsed.getTime())) errors.dueDateUtc = ['Enter a valid local date and time.']
    setFields(errors); setError('')
    if (Object.keys(errors).length) { (event.currentTarget.elements.namedItem(Object.keys(errors)[0]) as HTMLElement)?.focus(); return }
    lock.current = true; setPending(true)
    try {
      const task = await client.request<TaskItem>({ url: path + '/tasks', method: 'post', data: { title: title.trim(), description: description.trim() || null, priority, assigneeId: assignee || null, dueDateUtc: parsed?.toISOString() ?? null } })
      if (alive.current) navigate('/tasks/' + encodeURIComponent(task.id), { replace: true, state: { created: true } })
    } catch (failure) { if (alive.current) { const problem = asApiError(failure); setError(problem.message); setFields(problem.fields); window.setTimeout(() => summary.current?.focus(), 0) } }
    finally { lock.current = false; if (alive.current) setPending(false) }
  }
  const clearField = (field: string) => setFields(previous => ({ ...previous, [field]: [] }))
  const fieldError = (field: string) => fields[field]?.length ? <p className="field-error" id={field + '-error'}>{fields[field].join(' ')}</p> : null
  return <section className="projects-page project-form-page"><Link className="text-link" to={path + '/tasks'}>Back to tasks</Link><div className="page-heading"><div><p className="eyebrow">{project.name}</p><h1>Create a task</h1></div></div>
    <div className="project-form-card">{error && <div ref={summary} tabIndex={-1} className="error-summary" role="alert">{error}</div>}
      <form noValidate onSubmit={submit} aria-busy={pending}><fieldset disabled={pending}>
        <div className="form-field"><label htmlFor="task-title">Task title</label><input id="task-title" name="title" maxLength={200} value={title} onChange={event => { setTitle(event.target.value); clearField('title') }} aria-invalid={!!fields.title?.length} aria-describedby={fields.title?.length ? 'title-error' : undefined} />{fieldError('title')}</div>
        <div className="form-field"><label htmlFor="task-description">Description (optional)</label><textarea id="task-description" name="description" maxLength={10_000} rows={6} value={description} onChange={event => { setDescription(event.target.value); clearField('description') }} aria-invalid={!!fields.description?.length} aria-describedby={fields.description?.length ? 'description-error' : undefined} />{fieldError('description')}</div>
        <div className="form-field"><label htmlFor="task-priority">Priority</label><select id="task-priority" name="priority" value={priority} onChange={event => { setPriority(event.target.value); clearField('priority') }} aria-invalid={!!fields.priority?.length} aria-describedby={fields.priority?.length ? 'priority-error' : undefined}>{['Low', 'Medium', 'High'].map(value => <option key={value}>{value}</option>)}</select>{fieldError('priority')}</div>
        <div className="form-field"><label htmlFor="task-assignee">Assignee</label><select id="task-assignee" name="assigneeId" value={assignee} disabled={members.result.status !== 'ready'} onChange={event => { setAssignee(event.target.value); clearField('assigneeId') }} aria-invalid={!!fields.assigneeId?.length} aria-describedby={fields.assigneeId?.length ? 'assigneeId-error' : undefined}><option value="">Unassigned</option>{members.result.status === 'ready' && members.result.data.items.map(member => <option key={member.userId} value={member.userId}>{member.displayName} ({member.role})</option>)}</select>{fieldError('assigneeId')}
          {members.result.status === 'loading' && <p role="status">Loading assignees...</p>}{members.result.status === 'error' && <div><p role="alert">Could not load assignees. You can create an unassigned task.</p><button className="button subtle" type="button" onClick={members.retry}>Retry assignees</button></div>}</div>
        <div className="form-field"><label htmlFor="task-due">Due date and time (optional)</label><input id="task-due" name="dueDateUtc" type="datetime-local" step="1" value={due} onChange={event => { setDue(event.target.value); clearField('dueDateUtc') }} aria-invalid={!!fields.dueDateUtc?.length} aria-describedby={fields.dueDateUtc?.length ? 'dueDateUtc-error' : 'task-timezone'} /><p id="task-timezone" className="field-hint">Your local time ({Intl.DateTimeFormat().resolvedOptions().timeZone}). Saved in UTC.</p>{fieldError('dueDateUtc')}</div>
        <div className="form-actions"><button className="button primary" type="submit">{pending ? 'Creating task...' : 'Create task'}</button><button className="button subtle" type="button" onClick={() => navigate(path + '/tasks')}>Cancel</button></div>
      </fieldset></form>
    </div>
  </section>
}
export function TaskDetailPage({ client }: { client: ApiClient }) {
  const { taskId = '' } = useParams(); const location = useLocation()
  const { result, retry } = useProjectResource<TaskItem>(client, '/tasks/' + encodeURIComponent(taskId))
  const title = result.status === 'ready' ? result.data.title : null
  useEffect(() => { document.title = title ? title + ' | TaskFlow' : 'Task | TaskFlow' }, [title])
  return <section className="projects-page"><Link className="text-link" to={result.status === 'ready' ? '/projects/' + encodeURIComponent(result.data.projectId) + '/tasks' : '/projects'}>{result.status === 'ready' ? 'Back to tasks' : 'Back to projects'}</Link>
    {result.status === 'loading' && <p role="status">Loading task...</p>}{result.status === 'error' && <Failure status={result.error.status} message={result.error.message} retry={retry} />}
    {result.status === 'ready' && <>{location.state?.created && <p role="status" className="notice">Task created.</p>}<div className="page-heading"><h1 className="task-title">{result.data.title}</h1></div>
      <section className="project-about"><h2>Task details</h2><p className="project-description">{result.data.description || 'No description added.'}</p><dl className="task-details"><div><dt>Status</dt><dd>{statusText[result.data.status]}</dd></div><div><dt>Priority</dt><dd>{result.data.priority}</dd></div><div><dt>Assignee ID</dt><dd>{result.data.assigneeId ?? 'Unassigned'}</dd></div><div><dt>Deadline</dt><dd>{deadline(result.data.dueDateUtc)}</dd></div><div><dt>Created</dt><dd>{new Date(result.data.createdAtUtc).toLocaleString()}</dd></div></dl><p className="field-hint">Times are shown in your local time zone.</p></section>
      <Link className="text-link" to={'/projects/' + encodeURIComponent(result.data.projectId)}>View project progress</Link>
    </>}
  </section>
}
