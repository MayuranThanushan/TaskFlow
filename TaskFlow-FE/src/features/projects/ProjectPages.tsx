import { Confirmation, FieldError, RetryError, Pagination } from '../../components/Feedback'
import type { ProjectWrite } from './types'
import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from '../companies/navigation'
import { Badge, Icon } from '../../components/Workspace'
import { asApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import type { FieldErrors } from '../../api/types'
import { useAuth } from '../auth/auth-context'
import { ProjectMembers } from './ProjectMembers'
import { useApiResource } from '../../hooks/use-api-resource'
import type { Project, ProjectDetails, ProjectPage } from './types'

function date(value: string) { return new Date(value).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' }) }

function QueryForm({ params, apply }: { params: URLSearchParams; apply: (params: URLSearchParams) => void }) {
  const [search, setSearch] = useState(params.get('search') ?? '')
  const [sortBy, setSortBy] = useState(params.get('sortBy') ?? 'createdAtUtc')
  const [direction, setDirection] = useState(params.get('sortDirection') ?? 'desc')
  const [pageSize, setPageSize] = useState(params.get('pageSize') ?? '20')
  return <form className="project-filters" aria-label="Filter projects" onSubmit={event => {
    event.preventDefault()
    const next = new URLSearchParams({ sortBy, sortDirection: direction, pageSize })
    if (search.trim()) next.set('search', search.trim())
    apply(next)
  }}>
    <div><label htmlFor="project-search">Search projects</label><input id="project-search" type="search" maxLength={200} value={search} onChange={event => setSearch(event.target.value)} placeholder="Search by name" /></div>
    <div><label htmlFor="project-sort">Sort by</label><select id="project-sort" value={sortBy} onChange={event => setSortBy(event.target.value)}><option value="createdAtUtc">Created date</option><option value="updatedAtUtc">Updated date</option><option value="name">Name</option></select></div>
    <div><label htmlFor="project-direction">Order</label><select id="project-direction" value={direction} onChange={event => setDirection(event.target.value)}><option value="desc">Descending</option><option value="asc">Ascending</option></select></div>
    <div><label htmlFor="project-size">Per page</label><select id="project-size" value={pageSize} onChange={event => setPageSize(event.target.value)}>{[10, 20, 50, 100].map(size => <option key={size} value={size}>{size}</option>)}</select></div>
    <button className="button subtle" type="submit">Apply</button><Link className="text-link" to="/projects">Reset</Link>
  </form>
}

export function ProjectsPage({ client }: { client: ApiClient }) {
  const location = useLocation()
  const { session } = useAuth()
  const [params, setParams] = useSearchParams()
  const query = params.toString()
  const { result, retry } = useApiResource<ProjectPage>(client, '/projects' + (query ? '?' + query : ''))
  const changePage = (page: number) => { const next = new URLSearchParams(params); next.set('page', String(page)); setParams(next) }
  return <section className="projects-page">
    <div className="page-heading"><div><p className="eyebrow">Your workspace</p><h1>Projects</h1><p className="muted">Projects you own or work on.</p></div><Link className="button primary" to="/projects/new">New project</Link></div>
    {location.state?.deleted && <p role="status" className="notice">Project deleted.</p>}
    <QueryForm key={query} params={params} apply={setParams} />
    {result.status === 'loading' && <p role="status" className="project-state">Loading projects...</p>}
    {result.status === 'error' && <RetryError message={result.error.message} retry={retry} />}
    {result.status === 'ready' && <>
      <p className="result-count" role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'project' : 'projects'}</p>
      {result.data.items.length === 0 ? <div className="empty-workspace"><h2>{result.data.totalCount > 0 ? 'No projects on this page.' : params.get('search')?.trim() ? 'No projects match your search.' : 'Your first project starts here.'}</h2>
        <p>{result.data.totalCount > 0 ? 'Return to the first page to see your projects.' : params.get('search')?.trim() ? 'Try another name or reset the filters.' : 'Create a project to give your next idea a place to grow.'}</p>
        {result.data.totalCount > 0 && <button className="button subtle" onClick={() => changePage(1)}>Go to first page</button>}
      </div> : <ul className="project-grid">{result.data.items.map(project => <li className="project-card" key={project.id}>
        <div className="card-heading"><span className="project-symbol"><Icon name="projects" /></span><Badge value={project.ownerId === session.user?.id ? 'owner' : 'member'}>{project.ownerId === session.user?.id ? 'Owner' : 'Member'}</Badge></div><h2><Link to={'/projects/' + encodeURIComponent(project.id)}>{project.name}</Link></h2>
        <p className="project-excerpt">{project.description || 'No description added.'}</p>
        <p className="project-date">Updated <time dateTime={project.updatedAtUtc}>{date(project.updatedAtUtc)}</time></p><div className="card-links"><Link to={'/projects/' + project.id + '/tasks'}><Icon name="tasks" size={15} />Tasks</Link><Link to={'/projects/' + project.id + '/activity'}><Icon name="activity" size={15} />Activity</Link>{project.ownerId === session.user?.id && <Link to={'/projects/' + project.id + '/edit'}>Edit</Link>}</div>
      </li>)}</ul>}
      <Pagination label="Project pages" page={result.data.page} totalPages={result.data.totalPages} onPage={changePage} />
    </>}
  </section>
}

export function NewProjectPage({ client }: { client: ApiClient }) { return <ProjectForm client={client} /> }

export function EditProjectPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams()
  const { session } = useAuth()
  const { result, retry } = useApiResource<ProjectDetails>(client, '/projects/' + encodeURIComponent(projectId))
  return result.status === 'loading' ? <p role="status">Loading project...</p>
    : result.status === 'error' ? <section><h1>Project not available.</h1><p role="alert">{result.error.message}</p><Link to="/projects">Back to projects</Link>{result.error.status !== 404 && <button onClick={retry}>Try again</button>}</section>
    : session.user?.id !== result.data.ownerId ? <section><h1>Only the owner can edit this project.</h1><Link to={'/projects/' + encodeURIComponent(projectId)}>Back to project</Link></section>
    : <ProjectForm key={projectId} client={client} initial={result.data} />
}

function ProjectForm({ client, initial }: { client: ApiClient; initial?: Project }) {
  const navigate = useNavigate()
  const [name, setName] = useState(initial?.name ?? '')
  const [description, setDescription] = useState(initial?.description ?? '')
  const [fields, setFields] = useState<FieldErrors>({})
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)
  const locked = useRef(false)
  const summary = useRef<HTMLDivElement>(null)
  const alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (locked.current) return
    const errors: FieldErrors = {}
    if (!name.trim() || name.trim().length > 150) errors.name = ['Enter a name between 1 and 150 characters.']
    if (description.trim().length > 10_000) errors.description = ['Keep the description to 10,000 characters or fewer.']
    setFields(errors); setError('')
    if (Object.keys(errors).length) { (event.currentTarget.elements.namedItem(Object.keys(errors)[0]) as HTMLElement)?.focus(); return }
    locked.current = true; setPending(true)
    try {
      const project = await client.request<Project>({ url: initial ? '/projects/' + encodeURIComponent(initial.id) : '/projects', method: initial ? 'put' : 'post', data: { name: name.trim(), description: description.trim() || null } satisfies ProjectWrite })
      if (!alive.current) return
      navigate('/projects/' + encodeURIComponent(project.id), { replace: true, state: initial ? { updated: true } : { created: true } })
    } catch (failure) {
      if (!alive.current) return
      const problem = asApiError(failure); setError(problem.message); setFields(problem.fields)
      window.setTimeout(() => summary.current?.focus(), 0)
    } finally { locked.current = false; if (alive.current) setPending(false) }
  }
  return <section className="projects-page project-form-page"><Link className="text-link" to="/projects">Back to projects</Link>
    <div className="page-heading"><div><p className="eyebrow">A fresh start</p><h1>{initial ? 'Edit project' : 'Create a project'}</h1><p className="muted">Give your work a name and a little context.</p></div></div>
    <div className="project-form-card">{error && <div ref={summary} tabIndex={-1} className="error-summary" role="alert">{error}</div>}
      <p className="form-intro">{initial ? 'Keep the project brief clear and current. Changes are recorded in activity.' : 'Start with a clear name and a short brief. Add tasks and teammates once your project is ready.'}</p><form noValidate aria-busy={pending} onSubmit={submit}><fieldset disabled={pending}>
        <div className="form-field"><label htmlFor="project-name">Project name</label><input id="project-name" name="name" value={name} maxLength={150} aria-invalid={!!fields.name?.length} aria-describedby={fields.name?.length ? 'name-error' : undefined} onChange={event => { setName(event.target.value); setFields(previous => ({ ...previous, name: [] })) }} />
          <FieldError id="name-error" errors={fields.name} /></div>
        <div className="form-field"><label htmlFor="project-description">Description <span className="optional">(optional)</span></label><textarea id="project-description" name="description" rows={7} maxLength={10_000} value={description} aria-invalid={!!fields.description?.length} aria-describedby={fields.description?.length ? 'description-error' : 'description-hint'} onChange={event => { setDescription(event.target.value); setFields(previous => ({ ...previous, description: [] })) }} />
          <FieldError id="description-error" errors={fields.description} /><p id="description-hint" className="field-hint">Up to 10,000 characters. You can leave this blank.</p></div>
        <div className="form-actions"><button className="button primary" type="submit">{pending ? 'Saving project...' : initial ? 'Save changes' : 'Create project'}</button><button className="button subtle" type="button" onClick={() => navigate(initial ? '/projects/' + encodeURIComponent(initial.id) : '/projects')}>Cancel</button></div>
      </fieldset></form>
    </div>
  </section>
}

export function ProjectDetailPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams()
  const location = useLocation()
  const { session } = useAuth()
  const { result, retry } = useApiResource<ProjectDetails>(client, '/projects/' + encodeURIComponent(projectId))
  const name = result.status === 'ready' ? result.data.name : null
  useEffect(() => { document.title = name ? name + ' | TaskFlow' : 'Project | TaskFlow' }, [name])
  return <section className="projects-page project-detail project-detail-page"><Link className="text-link" to="/projects">Back to projects</Link>
    {result.status === 'loading' && <p role="status" className="project-state">Loading project...</p>}
    {result.status === 'error' && <div className="project-state"><h1>{result.error.status === 404 ? 'Project not available.' : result.error.status === 400 ? 'That project link is invalid.' : 'We could not load this project.'}</h1>
      <p role="alert">{result.error.status === 404 ? 'It may have been removed, or you may not have access.' : result.error.message}</p>
      {result.error.status !== 404 && result.error.status !== 400 && <button className="button subtle" onClick={retry}>Try again</button>}
    </div>}
    {result.status === 'ready' && <>
      {location.state?.updated && <p role="status" className="notice">Project updated.</p>}
      {location.state?.created && <p role="status" className="notice">Project created.</p>}
      <div className="page-heading"><div><p className="eyebrow">{session.user?.id === result.data.ownerId ? 'Your project' : 'Shared project'}</p><h1>{result.data.name}</h1><p className="muted">Created <time dateTime={result.data.createdAtUtc}>{date(result.data.createdAtUtc)}</time></p></div>{session.user?.id === result.data.ownerId && <ProjectActions key={projectId} client={client} project={result.data} />}</div>
      <section className="project-about" aria-labelledby="project-about"><h2 id="project-about">About this project</h2><p className="project-description">{result.data.description || 'No description added.'}</p></section>
      <section className="project-about"><h2>Tasks</h2><p>Plan the next steps and track your project work.</p><Link className="button primary" to={'/projects/' + encodeURIComponent(projectId) + '/tasks'}>View tasks</Link></section>
      <section className="project-about"><h2>Project history</h2><p>See changes to this project, its tasks and membership.</p><Link className="button subtle" to={'/projects/' + encodeURIComponent(projectId) + '/activity'}>View activity</Link></section>
      <ProjectMembers key={'members:' + projectId} client={client} projectId={projectId} owner={session.user?.id === result.data.ownerId} />
      <section className="project-progress" aria-labelledby="progress-heading"><h2 id="progress-heading">Project progress</h2><dl><div><dt>Total tasks</dt><dd>{result.data.totalTasks}</dd></div><div><dt>Completed tasks</dt><dd>{result.data.completedTasks}</dd></div><div><dt>Progress</dt><dd>{result.data.progressPercentage}%</dd></div></dl>
        <progress aria-label="Project progress" value={result.data.progressPercentage} max={100}>{result.data.progressPercentage}%</progress>
      </section>
    </>}
  </section>
}

function ProjectActions({ client, project }: { client: ApiClient; project: Project }) {
  const navigate = useNavigate()
  const [confirming, setConfirming] = useState(false)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const trigger = useRef<HTMLButtonElement>(null)
  const locked = useRef(false)
  const alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  function dismiss() { if (locked.current) return; setConfirming(false); setError(''); window.setTimeout(() => trigger.current?.focus(), 0) }
  async function remove() {
    if (locked.current) return
    locked.current = true; setPending(true); setError('')
    try {
      await client.request({ url: '/projects/' + encodeURIComponent(project.id), method: 'delete' })
      if (alive.current) navigate('/projects', { replace: true, state: { deleted: true } })
    } catch (failure) { if (alive.current) setError(asApiError(failure).message) }
    finally { locked.current = false; if (alive.current) setPending(false) }
  }
  return <div className="project-actions">
    {!confirming ? <><Link className="button subtle" to={'/projects/' + encodeURIComponent(project.id) + '/edit'}>Edit project</Link><button ref={trigger} className="button danger" onClick={() => setConfirming(true)}>Delete project</button></>
      : <Confirmation id="delete-heading" title="Delete this project?" pending={pending} cancelLabel="Keep project" confirmLabel="Confirm deletion" pendingLabel="Deleting project..." onCancel={dismiss} onConfirm={() => void remove()}><p>Deleting <strong>{project.name}</strong> removes access to the project and its tasks. This cannot be undone here.</p>{error && <p role="alert">{error}</p>}</Confirmation>}
  </div>
}
