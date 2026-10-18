import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useLocation, useNavigate, useParams, useSearchParams } from 'react-router-dom'
import { asApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import type { FieldErrors } from '../../api/types'
import { useAuth } from '../auth/auth-context'
import { useProjectResource } from './use-project-resource'
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
  const [params, setParams] = useSearchParams()
  const query = params.toString()
  const { result, retry } = useProjectResource<ProjectPage>(client, '/projects' + (query ? '?' + query : ''))
  const changePage = (page: number) => { const next = new URLSearchParams(params); next.set('page', String(page)); setParams(next) }
  return <section className="projects-page">
    <div className="page-heading"><div><p className="eyebrow">Your workspace</p><h1>Projects</h1><p className="muted">Projects you own or work on.</p></div><Link className="button primary" to="/projects/new">New project</Link></div>
    <QueryForm key={query} params={params} apply={setParams} />
    {result.status === 'loading' && <p role="status" className="project-state">Loading projects...</p>}
    {result.status === 'error' && <div className="error-summary" role="alert"><p>{result.error.message}</p><button className="button subtle" onClick={retry}>Try again</button></div>}
    {result.status === 'ready' && <>
      <p className="result-count" role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'project' : 'projects'}</p>
      {result.data.items.length === 0 ? <div className="empty-workspace"><h2>{result.data.totalCount > 0 ? 'No projects on this page.' : params.get('search')?.trim() ? 'No projects match your search.' : 'Your first project starts here.'}</h2>
        <p>{result.data.totalCount > 0 ? 'Return to the first page to see your projects.' : params.get('search')?.trim() ? 'Try another name or reset the filters.' : 'Create a project to give your next idea a place to grow.'}</p>
        {result.data.totalCount > 0 && <button className="button subtle" onClick={() => changePage(1)}>Go to first page</button>}
      </div> : <ul className="project-grid">{result.data.items.map(project => <li className="project-card" key={project.id}>
        <h2><Link to={'/projects/' + encodeURIComponent(project.id)}>{project.name}</Link></h2>
        <p className="project-excerpt">{project.description || 'No description added.'}</p>
        <p className="project-date">Created <time dateTime={project.createdAtUtc}>{date(project.createdAtUtc)}</time></p>
      </li>)}</ul>}
      {result.data.totalPages > 1 && <nav className="pagination" aria-label="Project pages"><button className="button subtle" disabled={result.data.page <= 1} onClick={() => changePage(result.data.page - 1)}>Previous</button>
        <span>Page {result.data.page} of {result.data.totalPages}</span><button className="button subtle" disabled={result.data.page >= result.data.totalPages} onClick={() => changePage(result.data.page + 1)}>Next</button></nav>}
    </>}
  </section>
}

export function NewProjectPage({ client }: { client: ApiClient }) {
  const navigate = useNavigate()
  const [name, setName] = useState('')
  const [description, setDescription] = useState('')
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
      const project = await client.request<Project>({ url: '/projects', method: 'post', data: { name: name.trim(), description: description.trim() || null } })
      if (!alive.current) return
      navigate('/projects/' + encodeURIComponent(project.id), { replace: true, state: { created: true } })
    } catch (failure) {
      if (!alive.current) return
      const problem = asApiError(failure); setError(problem.message); setFields(problem.fields)
      window.setTimeout(() => summary.current?.focus(), 0)
    } finally { locked.current = false; if (alive.current) setPending(false) }
  }
  return <section className="projects-page project-form-page"><Link className="text-link" to="/projects">Back to projects</Link>
    <div className="page-heading"><div><p className="eyebrow">A fresh start</p><h1>Create a project</h1><p className="muted">Give your work a name and a little context.</p></div></div>
    <div className="project-form-card">{error && <div ref={summary} tabIndex={-1} className="error-summary" role="alert">{error}</div>}
      <form noValidate aria-busy={pending} onSubmit={submit}><fieldset disabled={pending}>
        <div className="form-field"><label htmlFor="project-name">Project name</label><input id="project-name" name="name" value={name} maxLength={150} aria-invalid={!!fields.name?.length} aria-describedby={fields.name?.length ? 'name-error' : undefined} onChange={event => { setName(event.target.value); setFields(previous => ({ ...previous, name: [] })) }} />
          {!!fields.name?.length && <p className="field-error" id="name-error">{fields.name.join(' ')}</p>}</div>
        <div className="form-field"><label htmlFor="project-description">Description <span className="optional">(optional)</span></label><textarea id="project-description" name="description" rows={7} maxLength={10_000} value={description} aria-invalid={!!fields.description?.length} aria-describedby={fields.description?.length ? 'description-error' : 'description-hint'} onChange={event => { setDescription(event.target.value); setFields(previous => ({ ...previous, description: [] })) }} />
          {!!fields.description?.length && <p className="field-error" id="description-error">{fields.description.join(' ')}</p>}<p id="description-hint" className="field-hint">Up to 10,000 characters. You can leave this blank.</p></div>
        <div className="form-actions"><button className="button primary" type="submit">{pending ? 'Creating project...' : 'Create project'}</button><button className="button subtle" type="button" onClick={() => navigate('/projects')}>Cancel</button></div>
      </fieldset></form>
    </div>
  </section>
}

export function ProjectDetailPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams()
  const location = useLocation()
  const { session } = useAuth()
  const { result, retry } = useProjectResource<ProjectDetails>(client, '/projects/' + encodeURIComponent(projectId))
  const name = result.status === 'ready' ? result.data.name : null
  useEffect(() => { document.title = name ? name + ' | TaskFlow' : 'Project | TaskFlow' }, [name])
  return <section className="projects-page project-detail"><Link className="text-link" to="/projects">Back to projects</Link>
    {result.status === 'loading' && <p role="status" className="project-state">Loading project...</p>}
    {result.status === 'error' && <div className="project-state"><h1>{result.error.status === 404 ? 'Project not available.' : result.error.status === 400 ? 'That project link is invalid.' : 'We could not load this project.'}</h1>
      <p role="alert">{result.error.status === 404 ? 'It may have been removed, or you may not have access.' : result.error.message}</p>
      {result.error.status !== 404 && result.error.status !== 400 && <button className="button subtle" onClick={retry}>Try again</button>}
    </div>}
    {result.status === 'ready' && <>
      {location.state?.created && <p role="status" className="notice">Project created.</p>}
      <div className="page-heading"><div><p className="eyebrow">{session.user?.id === result.data.ownerId ? 'Your project' : 'Shared project'}</p><h1>{result.data.name}</h1><p className="muted">Created <time dateTime={result.data.createdAtUtc}>{date(result.data.createdAtUtc)}</time></p></div></div>
      <section className="project-about" aria-labelledby="project-about"><h2 id="project-about">About this project</h2><p className="project-description">{result.data.description || 'No description added.'}</p></section>
      <section className="project-progress" aria-labelledby="progress-heading"><h2 id="progress-heading">Project progress</h2><dl><div><dt>Total tasks</dt><dd>{result.data.totalTasks}</dd></div><div><dt>Completed tasks</dt><dd>{result.data.completedTasks}</dd></div><div><dt>Progress</dt><dd>{result.data.progressPercentage}%</dd></div></dl>
        <progress aria-label="Project progress" value={result.data.progressPercentage} max={100}>{result.data.progressPercentage}%</progress>
      </section>
    </>}
  </section>
}
