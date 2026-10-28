import { Pagination, TableRegion, FieldError, Confirmation } from '../../components/Feedback'
import type { TaskCreate, TaskPatch, TaskStatusUpdate } from './types'
import { Badge } from '../../components/Workspace'
import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from '../companies/navigation'
import { useAuth } from '../auth/auth-context'
import { asApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import type { FieldErrors } from '../../api/types'
import { useApiResource } from '../../hooks/use-api-resource'
import type { ProjectDetails } from '../projects/types'
import type { TaskItem, TaskPage } from './types'

const statusText = { ToDo: 'To Do', InProgress: 'In Progress', Done: 'Completed' }
function deadline(value: string | null) { return value ? new Date(value).toLocaleString() : 'No deadline' }
function Failure({ status, message, retry }: { status: number | undefined; message: string; retry: () => void }) {
  return <div className="project-state"><h1>{status === 404 ? 'Task or project not available.' : status === 400 ? 'That task link is invalid.' : 'We could not load this task view.'}</h1><p role="alert">{status === 404 ? 'It may have been removed, or you may not have access.' : message}</p>{status !== 404 && status !== 400 && <button className="button subtle" onClick={retry}>Try again</button>}</div>
}
type TaskAssignee = { userId: string; displayName: string; role: string }
function TaskQuery({ params, apply, assignees, assigneesFailed, retryAssignees }: { params: URLSearchParams; apply: (next: URLSearchParams) => void; assignees: TaskAssignee[]; assigneesFailed: boolean; retryAssignees: () => void }) {
  const [search, setSearch] = useState(params.get('search') ?? '')
  const [pageSize, setPageSize] = useState(params.get('pageSize') ?? '20')
  const [status, setStatus] = useState(params.get('status') ?? '')
  const [priority, setPriority] = useState(params.get('priority') ?? '')
  const [assignee, setAssignee] = useState(params.get('assigneeId') ?? '')
  const [sortBy, setSortBy] = useState(params.get('sortBy') ?? 'createdAtUtc')
  const [direction, setDirection] = useState(params.get('sortDirection') ?? 'desc')
  return <form className="project-filters" aria-label="Find tasks" onSubmit={event => {
    event.preventDefault()
    const next = new URLSearchParams({ pageSize, sortBy, sortDirection: direction })
    if (search.trim()) next.set('search', search.trim())
    if (status) next.set('status', status)
    if (priority) next.set('priority', priority)
    if (assignee) next.set('assigneeId', assignee)
    apply(next)
  }}>
    <div><label htmlFor="task-search">Search tasks</label><input id="task-search" type="search" maxLength={200} value={search} onChange={event => setSearch(event.target.value)} /></div>
    <div><label htmlFor="task-filter-status">Filter by status</label><select id="task-filter-status" value={status} onChange={event => setStatus(event.target.value)}><option value="">All statuses</option>{Object.entries(statusText).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select></div>
    <div><label htmlFor="task-filter-priority">Filter by priority</label><select id="task-filter-priority" value={priority} onChange={event => setPriority(event.target.value)}><option value="">All priorities</option>{['Low', 'Medium', 'High'].map(value => <option key={value}>{value}</option>)}</select></div>
    <div><label htmlFor="task-filter-assignee">Filter by assignee</label><select id="task-filter-assignee" value={assignee} onChange={event => setAssignee(event.target.value)}><option value="">All assignees</option>{assignee && !assignees.some(member => member.userId === assignee) && <option value={assignee}>User ID: {assignee}</option>}{assignees.map(member => <option key={member.userId} value={member.userId}>{member.displayName} ({member.role})</option>)}</select>{assigneesFailed && <><p className="field-hint">Assignee names could not be loaded. Other filters still work.</p><button className="text-button" type="button" onClick={retryAssignees}>Retry assignee names</button></>}</div>
    <div><label htmlFor="task-sort">Sort tasks by</label><select id="task-sort" value={sortBy} onChange={event => setSortBy(event.target.value)}><option value="createdAtUtc">Created date</option><option value="title">Title</option><option value="priority">Priority</option><option value="dueDateUtc">Deadline</option></select></div>
    <div><label htmlFor="task-direction">Task order</label><select id="task-direction" value={direction} onChange={event => setDirection(event.target.value)}><option value="desc">Descending</option><option value="asc">Ascending</option></select></div>
    <div><label htmlFor="task-page-size">Per page</label><select id="task-page-size" value={pageSize} onChange={event => setPageSize(event.target.value)}>{[10, 20, 50, 100].map(size => <option key={size}>{size}</option>)}</select></div>
    <button className="button subtle">Apply</button><button className="button subtle" type="button" onClick={() => apply(new URLSearchParams())}>Reset</button>
  </form>
}
export function TasksPage({ client }: { client: ApiClient }) {
  const location = useLocation()
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 60_000); return () => window.clearInterval(timer) }, [])
  const { projectId = '' } = useParams(); const [params, setParams] = useSearchParams(); const query = params.toString()
  const path = '/projects/' + encodeURIComponent(projectId)
  const members = useApiResource<{ items: TaskAssignee[] }>(client, path + '/members')
  const filtered = ['search', 'status', 'priority', 'assigneeId'].some(key => params.get(key)?.trim())
  const { result, retry } = useApiResource<TaskPage>(client, path + '/tasks' + (query ? '?' + query : ''))
  const page = (value: number) => { const next = new URLSearchParams(params); next.set('page', String(value)); setParams(next) }
  return <section className="projects-page"><Link className="text-link" to={path}>Back to project</Link>
    <div className="page-heading"><div><p className="eyebrow">Project work</p><h1>Tasks</h1></div><Link className="button primary" to={path + '/tasks/new'}>New task</Link></div>
    {location.state?.deleted && <p role="status" className="notice">Task deleted.</p>}
    <TaskQuery key={query} params={params} apply={setParams} assignees={members.result.status === 'ready' ? members.result.data.items : []} assigneesFailed={members.result.status === 'error'} retryAssignees={members.retry} />
    {result.status === 'loading' && <p role="status">Loading tasks...</p>}
    {result.status === 'error' && (result.error.status === 400 ? <div className="error-summary" role="alert"><p>These task filters are invalid. Reset the filters or choose supported values and apply again.</p><button className="button subtle" onClick={() => setParams(new URLSearchParams())}>Reset invalid filters</button></div> : <Failure status={result.error.status} message={result.error.message} retry={retry} />)}
    {result.status === 'ready' && <><p role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'task' : 'tasks'}</p>
      {result.data.items.length === 0 ? <div className="empty-workspace"><h2>{result.data.totalCount > 0 ? 'No tasks on this page.' : filtered ? 'No tasks match your filters.' : 'No tasks yet.'}</h2><p>Create a task or adjust your filters to find work here.</p>{result.data.totalCount > 0 && <button className="button subtle" onClick={() => page(1)}>Go to first page</button>}</div>
         : <TableRegion label="Project task table, scroll horizontally for more columns"><table className="task-table"><caption className="sr-only">Project tasks and their current status, priority, assignee and deadline</caption><thead><tr><th scope="col">Task name</th><th scope="col">Status</th><th scope="col">Priority</th><th scope="col">Assignee</th><th scope="col">Due date</th><th scope="col">Actions</th></tr></thead><tbody>{result.data.items.map(task => <TaskRow key={task.id} task={task} client={client} now={now} members={members.result.status === 'ready' ? members.result.data.items : []} refresh={retry} />)}</tbody></table></TableRegion>}
      <Pagination label="Task pages" page={result.data.page} totalPages={result.data.totalPages} onPage={page} />
    </>}
  </section>
}
function TaskRow({ task, client, now, members, refresh }: { task: TaskItem; client: ApiClient; now: number; members: TaskAssignee[]; refresh: () => void }) {
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const lock = useRef(false)
  const alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  async function changeStatus(status: TaskItem['status']) {
    if (lock.current || status === task.status) return
    lock.current = true; setPending(true); setError('')
    try {
      await client.request({ url: '/tasks/' + encodeURIComponent(task.id) + '/status', method: 'patch', data: { status } satisfies TaskStatusUpdate })
      if (alive.current) refresh()
    } catch (failure) { if (alive.current) setError(asApiError(failure).message) }
    finally { lock.current = false; if (alive.current) setPending(false) }
  }
  return <tr aria-busy={pending}><td><Link className="task-name" to={'/tasks/' + encodeURIComponent(task.id)}>{task.title}</Link>{task.description && <span className="task-description-preview">{task.description}</span>}{error && <p className="field-error" role="alert">{error}</p>}</td>
    <td><select className={'inline-status badge-' + task.status.toLowerCase()} aria-label={'Status for ' + task.title} disabled={pending} value={task.status} onChange={event => void changeStatus(event.target.value as TaskItem['status'])}>{Object.entries(statusText).map(([value, label]) => <option key={value} value={value}>{label}</option>)}</select>{pending && <span className="sr-only" role="status">Saving task status...</span>}</td>
    <td><Badge value={task.priority}>{task.priority}</Badge></td><td>{task.assigneeId ? members.find(member => member.userId === task.assigneeId)?.displayName ?? 'User ' + task.assigneeId : <span className="unassigned">Unassigned</span>}</td>
    <td><span className={task.dueDateUtc && new Date(task.dueDateUtc).getTime() < now && task.status !== 'Done' ? 'overdue-label' : ''}>{deadline(task.dueDateUtc)}</span></td>
    <td><Link className="row-action" aria-label={'Edit ' + task.title} to={'/tasks/' + task.id + '/edit'}>Edit</Link><Link className="row-action" aria-label={'Open ' + task.title} to={'/tasks/' + task.id}>Open</Link></td></tr>
}

export function NewTaskPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams()
  const { result, retry } = useApiResource<ProjectDetails>(client, '/projects/' + encodeURIComponent(projectId))
  return result.status === 'loading' ? <p role="status">Loading project...</p> : result.status === 'error' ? <><Link to="/projects">Back to projects</Link><Failure status={result.error.status} message={result.error.message} retry={retry} /></>
    : <TaskForm key={projectId} client={client} project={result.data} />
}
export function EditTaskPage({ client }: { client: ApiClient }) {
  const { taskId = '' } = useParams()
  const { result, retry } = useApiResource<TaskItem>(client, '/tasks/' + encodeURIComponent(taskId))
  return result.status === 'loading' ? <p role="status">Loading task...</p> : result.status === 'error' ? <><Link to="/projects">Back to projects</Link><Failure status={result.error.status} message={result.error.message} retry={retry} /></>
    : <EditTaskForm key={taskId} client={client} task={result.data} />
}
function EditTaskForm({ client, task }: { client: ApiClient; task: TaskItem }) {
  const { result, retry } = useApiResource<ProjectDetails>(client, '/projects/' + encodeURIComponent(task.projectId))
  return result.status === 'loading' ? <p role="status">Loading project...</p> : result.status === 'error' ? <Failure status={result.error.status} message={result.error.message} retry={retry} />
    : <TaskForm client={client} project={result.data} initial={task} />
}
function localDeadline(value: string | null) {
  if (!value) return ''
  const date = new Date(value)
  return new Date(date.getTime() - date.getTimezoneOffset() * 60_000).toISOString().slice(0, 23)
}
function TaskForm({ client, project, initial }: { client: ApiClient; project: ProjectDetails; initial?: TaskItem }) {
  const navigate = useNavigate(); const path = '/projects/' + encodeURIComponent(project.id)
  const members = useApiResource<{ items: { userId: string; displayName: string; role: string }[] }>(client, path + '/members')
  const [title, setTitle] = useState(initial?.title ?? ''); const [description, setDescription] = useState(initial?.description ?? ''); const [priority, setPriority] = useState(initial?.priority ?? 'Medium')
  const [assignee, setAssignee] = useState(initial?.assigneeId ?? ''); const [due, setDue] = useState(localDeadline(initial?.dueDateUtc ?? null)); const [fields, setFields] = useState<FieldErrors>({})
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
      const values: TaskCreate = { title: title.trim(), description: description.trim() || null, priority, assigneeId: assignee || null, dueDateUtc: parsed?.toISOString() ?? null }
      const data: TaskPatch = initial ? {} : values
      if (initial) {
        if (values.title !== initial.title) data.title = values.title
        if (values.description !== initial.description) data.description = values.description
        if (values.priority !== initial.priority) data.priority = values.priority
        if (values.assigneeId !== initial.assigneeId) data.assigneeId = values.assigneeId
        // Omit an untouched deadline to preserve microseconds and ambiguous local instants.
        if (due !== localDeadline(initial.dueDateUtc)) data.dueDateUtc = values.dueDateUtc
        if (Object.keys(data).length === 0) { navigate('/tasks/' + encodeURIComponent(initial.id), { replace: true }); return }
      }
      const task = await client.request<TaskItem>({ url: initial ? '/tasks/' + encodeURIComponent(initial.id) : path + '/tasks', method: initial ? 'patch' : 'post', data })
      if (alive.current) navigate('/tasks/' + encodeURIComponent(task.id), { replace: true, state: initial ? { updated: true } : { created: true } })
    } catch (failure) { if (alive.current) { const problem = asApiError(failure); setError(problem.message); setFields(problem.fields); window.setTimeout(() => summary.current?.focus(), 0) } }
    finally { lock.current = false; if (alive.current) setPending(false) }
  }
  const clearField = (field: string) => setFields(previous => ({ ...previous, [field]: [] }))
  const fieldError = (field: string) => <FieldError id={field + '-error'} errors={fields[field]} />
  return <section className="projects-page project-form-page"><Link className="text-link" to={path + '/tasks'}>Back to tasks</Link><div className="page-heading"><div><p className="eyebrow">{project.name}</p><h1>{initial ? 'Edit task' : 'Create a task'}</h1></div></div>
    <div className="project-form-card">{error && <div ref={summary} tabIndex={-1} className="error-summary" role="alert">{error}</div>}
      <p className="form-intro">{initial ? 'Refine the task details below. Your changes appear in project activity.' : 'Give your task a clear outcome, then choose its priority, owner and deadline.'}</p><form noValidate onSubmit={submit} aria-busy={pending}><fieldset className="task-form-fields" disabled={pending}>
        <div className="form-field"><label htmlFor="task-title">Task title</label><input id="task-title" name="title" aria-required="true" maxLength={200} value={title} onChange={event => { setTitle(event.target.value); clearField('title') }} aria-invalid={!!fields.title?.length} aria-describedby={fields.title?.length ? 'title-error' : undefined} />{fieldError('title')}</div>
        <div className="form-field"><label htmlFor="task-description">Description (optional)</label><textarea id="task-description" name="description" maxLength={10_000} rows={6} value={description} onChange={event => { setDescription(event.target.value); clearField('description') }} aria-invalid={!!fields.description?.length} aria-describedby={fields.description?.length ? 'description-error' : undefined} />{fieldError('description')}</div>
        <div className="form-field"><label htmlFor="task-priority">Priority</label><select id="task-priority" name="priority" value={priority} onChange={event => { setPriority(event.target.value as TaskItem['priority']); clearField('priority') }} aria-invalid={!!fields.priority?.length} aria-describedby={fields.priority?.length ? 'priority-error' : undefined}>{['Low', 'Medium', 'High'].map(value => <option key={value}>{value}</option>)}</select>{fieldError('priority')}</div>
        <div className="form-field"><label htmlFor="task-assignee">Assignee</label><select id="task-assignee" name="assigneeId" value={assignee} disabled={members.result.status !== 'ready'} onChange={event => { setAssignee(event.target.value); clearField('assigneeId') }} aria-invalid={!!fields.assigneeId?.length} aria-describedby={fields.assigneeId?.length ? 'assigneeId-error' : undefined}><option value="">Unassigned</option>{assignee && (members.result.status !== 'ready' || !members.result.data.items.some(member => member.userId === assignee)) && <option value={assignee}>Current assignee ({assignee})</option>}{members.result.status === 'ready' && members.result.data.items.map(member => <option key={member.userId} value={member.userId}>{member.displayName} ({member.role})</option>)}</select>{fieldError('assigneeId')}
          {members.result.status === 'loading' && <p role="status">Loading assignees...</p>}{members.result.status === 'error' && <div><p role="alert">Could not load assignees. Retry to choose an assignee.</p><button className="button subtle" type="button" onClick={members.retry}>Retry assignees</button></div>}</div>
        <div className="form-field"><label htmlFor="task-due">Due date and time (optional)</label><input id="task-due" name="dueDateUtc" type="datetime-local" step="0.001" value={due} onChange={event => { setDue(event.target.value); clearField('dueDateUtc') }} aria-invalid={!!fields.dueDateUtc?.length} aria-describedby={fields.dueDateUtc?.length ? 'dueDateUtc-error' : 'task-timezone'} /><p id="task-timezone" className="field-hint">Your local time ({Intl.DateTimeFormat().resolvedOptions().timeZone}). Saved in UTC.</p>{fieldError('dueDateUtc')}</div>
        <div className="form-actions"><button className="button primary" type="submit">{pending ? initial ? 'Saving changes...' : 'Creating task...' : initial ? 'Save changes' : 'Create task'}</button><button className="button subtle" type="button" onClick={() => navigate(initial ? '/tasks/' + encodeURIComponent(initial.id) : path + '/tasks')}>Cancel</button></div>
      </fieldset></form>
    </div>
  </section>
}
export function TaskDetailPage({ client }: { client: ApiClient }) {
  const { taskId = '' } = useParams(); const location = useLocation()
  const { result, retry } = useApiResource<TaskItem>(client, '/tasks/' + encodeURIComponent(taskId))
  const title = result.status === 'ready' ? result.data.title : null
  useEffect(() => { document.title = title ? title + ' | TaskFlow' : 'Task | TaskFlow' }, [title])
  return <section className="projects-page"><Link className="text-link" to={result.status === 'ready' ? '/projects/' + encodeURIComponent(result.data.projectId) + '/tasks' : '/projects'}>{result.status === 'ready' ? 'Back to tasks' : 'Back to projects'}</Link>
    {result.status === 'loading' && <p role="status">Loading task...</p>}{result.status === 'error' && <Failure status={result.error.status} message={result.error.message} retry={retry} />}
    {result.status === 'ready' && <>{location.state?.created && <p role="status" className="notice">Task created.</p>}{location.state?.updated && <p role="status" className="notice">Task updated.</p>}{location.state?.statusUpdated && <p role="status" className="notice">Task status updated.</p>}<div className="page-heading"><h1 className="task-title">{result.data.title}</h1></div>
      <TaskActions key={taskId} client={client} task={result.data} refresh={retry} />
      <section className="project-about"><h2>Task details</h2><p className="project-description">{result.data.description || 'No description added.'}</p><dl className="task-details"><div><dt>Status</dt><dd><Badge value={result.data.status}>{statusText[result.data.status]}</Badge></dd></div><div><dt>Priority</dt><dd><Badge value={result.data.priority} /></dd></div><div><dt>Assignee ID</dt><dd>{result.data.assigneeId ?? 'Unassigned'}</dd></div><div><dt>Deadline</dt><dd>{deadline(result.data.dueDateUtc)}</dd></div><div><dt>Created</dt><dd>{new Date(result.data.createdAtUtc).toLocaleString()}</dd></div></dl><p className="field-hint">Times are shown in your local time zone.</p></section>
      <Link className="text-link" to={'/projects/' + encodeURIComponent(result.data.projectId)}>View project progress</Link>
    </>}
  </section>
}

function TaskActions({ client, task, refresh }: { client: ApiClient; task: TaskItem; refresh: () => void }) {
  const { session } = useAuth(); const navigate = useNavigate()
  const project = useApiResource<ProjectDetails>(client, '/projects/' + encodeURIComponent(task.projectId))
  const [status, setStatus] = useState(task.status); const [confirm, setConfirm] = useState(false)
  const [pending, setPending] = useState(false); const [error, setError] = useState('')
  const alive = useRef(true); const lock = useRef(false); const trigger = useRef<HTMLButtonElement>(null)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  async function mutate(remove: boolean) {
    if (lock.current) return
    lock.current = true; setPending(true); setError('')
    try {
      await client.request({ url: '/tasks/' + encodeURIComponent(task.id) + (remove ? '' : '/status'), method: remove ? 'delete' : 'patch', ...(remove ? {} : { data: { status } satisfies TaskStatusUpdate }) })
      if (!alive.current) return
      if (remove) navigate('/projects/' + encodeURIComponent(task.projectId) + '/tasks', { replace: true, state: { deleted: true } })
      else { navigate('/tasks/' + encodeURIComponent(task.id), { replace: true, state: { statusUpdated: true } }); refresh() }
    } catch (failure) { if (alive.current) setError(asApiError(failure).message) }
    finally { lock.current = false; if (alive.current) setPending(false) }
  }
  function dismiss() { if (!lock.current) { setConfirm(false); setError(''); window.setTimeout(() => trigger.current?.focus(), 0) } }
  return <section className="task-actions" aria-label="Task actions">
    {error && <p role="alert" className="error-summary">{error}</p>}
    <div className="form-actions"><Link className="button subtle" to={'/tasks/' + encodeURIComponent(task.id) + '/edit'}>Edit task</Link>
      {project.result.status === 'ready' && project.result.data.ownerId === session.user?.id && <button ref={trigger} className="button danger" disabled={pending || confirm} onClick={() => setConfirm(true)}>Delete task</button>}</div>
    {project.result.status === 'error' && <button className="button subtle" onClick={project.retry}>Retry owner controls</button>}
    {!confirm && <form className="task-status" onSubmit={event => { event.preventDefault(); void mutate(false) }}><label htmlFor="task-status">Task status</label><select id="task-status" value={status} disabled={pending} onChange={event => setStatus(event.target.value as TaskItem['status'])}>{Object.entries(statusText).map(([value, label]) => <option value={value} key={value}>{label}</option>)}</select><button className="button primary" disabled={pending || status === task.status}>{pending ? 'Updating status...' : 'Update status'}</button></form>}
    {confirm && <Confirmation id="delete-task-heading" title="Delete this task?" pending={pending} cancelLabel="Keep task" confirmLabel="Confirm task deletion" pendingLabel="Deleting task..." onCancel={dismiss} onConfirm={() => void mutate(true)}><p>Deleting <strong>{task.title}</strong> removes it from task lists and project progress. This cannot be undone here.</p></Confirmation>}
  </section>
}
