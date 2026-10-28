import { useEffect, useMemo, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, Route, Routes, useNavigate, useParams } from 'react-router-dom'
import { asApiError } from '../../api/client'
import type { components } from '../../api/generated'
import type { ApiClient } from '../../api/client'
import { useAuth } from '../auth/auth-context'
import { ProjectsPage, NewProjectPage, ProjectDetailPage, EditProjectPage } from '../projects/ProjectPages'
import { TasksPage, NewTaskPage, TaskDetailPage, EditTaskPage } from '../tasks/TaskPages'
import { DashboardPage } from '../dashboard/DashboardPage'
import { ActivityPage } from '../activity/ActivityPage'
import { ActivityHub } from '../activity/ActivityHub'
import { CompanyContext } from './company-context'
import { CompanyTeamPage, CompanyAuditPage } from './LifecyclePages'
import { Pagination, RetryError } from '../../components/Feedback'

export type Company = components['schemas']['WorkspaceDto']
type CompanyPage = components['schemas']['WorkspacePage']

export function CompanyDirectory({ client }: { client: ApiClient }) {
  const { session } = useAuth()
  return <CompaniesPage key={session.user?.id} client={client} />
}

export function CompaniesPage({ client }: { client: ApiClient }) {
  const [page, setPage] = useState(1)
  const [attempt, setAttempt] = useState(0)
  const [result, setResult] = useState<CompanyPage>()
  const [error, setError] = useState('')
  const [name, setName] = useState('')
  const [pending, setPending] = useState(false)
  const [saveError, setSaveError] = useState('')
  const lock = useRef(false)
  const alive = useRef(false)
  const navigate = useNavigate()
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  useEffect(() => {
    const controller = new AbortController()
    let active = true
    void client.directory<CompanyPage>({ url: '/workspaces?page=' + page + '&pageSize=20', signal: controller.signal }).then(data => {
      if (active) setResult(data)
    }).catch(failure => { if (active) setError(asApiError(failure).status === 404 ? 'Company preview is not enabled on this server.' : asApiError(failure).message) })
    return () => { active = false; controller.abort() }
  }, [client, page, attempt])
  const create = async (event: FormEvent) => {
    event.preventDefault()
    if (lock.current) return
    lock.current = true; setPending(true); setSaveError('')
    try {
      const company = await client.directory<Company>({ url: '/workspaces', method: 'post', data: { name: name.trim() } })
      if (alive.current) void navigate('/companies/' + company.id + '/dashboard')
    } catch (failure) { if (alive.current) setSaveError(asApiError(failure).message) }
    finally { lock.current = false; if (alive.current) setPending(false) }
  }
  return <section className="projects-page"><div className="page-heading"><div><p className="eyebrow">Your organizations</p><h1>Companies</h1><p className="muted">Choose a company to open its projects. Switching clears open forms and filters.</p></div></div>
    {error ? <RetryError message={error} retry={() => { setError(''); setResult(undefined); setAttempt(a => a + 1) }} /> : !result ? <p role="status">Loading companies...</p> : <>
      <ul className="project-grid">{result.items.map(company => <li className="project-card" key={company.id}><span className="badge">{company.role}</span><h2>{company.name}</h2><Link className="button primary" to={'/companies/' + company.id + '/dashboard'}>Open {company.name}</Link></li>)}</ul>
      {result.totalCount === 0 && <p>You do not belong to a company yet. Create one below to get started.</p>}
      <Pagination label="Company pages" page={result.page} totalPages={result.totalPages} onPage={p => { setResult(undefined); setPage(p) }} />
    </>}
    <form className="project-card company-create" aria-label="Create company" onSubmit={event => void create(event)}><h2>Create a company</h2><p>You will be its owner. Other companies and their projects stay separate.</p>
      <div className="form-field"><label htmlFor="company-name">Company name</label><input id="company-name" required maxLength={150} value={name} onChange={event => setName(event.target.value)} disabled={pending} /></div>
      {saveError && <p role="alert">{saveError}</p>}<button className="button primary" disabled={pending || !name.trim()}>{pending ? 'Creating company...' : 'Create company'}</button>
    </form>
  </section>
}

export function CompanyArea({ client, lifecycleEnabled = false }: { client: ApiClient; lifecycleEnabled?: boolean }) {
  const { workspaceId = '' } = useParams()
  const { session } = useAuth()
  if (!/^[1-9][0-9]*$/.test(workspaceId) || BigInt(workspaceId) > 9223372036854775807n) return <section><h1>Company not available.</h1><Link to="/companies">Choose a company</Link></section>
  return <CompanyContent key={session.user?.id + ':' + workspaceId} client={client} workspaceId={workspaceId} lifecycleEnabled={lifecycleEnabled} />
}

function CompanyContent({ client, workspaceId, lifecycleEnabled }: { client: ApiClient; workspaceId: string; lifecycleEnabled: boolean }) {
  const [company, setCompany] = useState<Company>()
  const [error, setError] = useState('')
  const [attempt, setAttempt] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    let active = true
    void client.directory<Company>({ url: '/workspaces/' + workspaceId, signal: controller.signal }).then(data => { if (active) setCompany(data) })
      .catch(failure => { if (active) setError(asApiError(failure).status === 404 ? 'This company is unavailable or your access has changed.' : asApiError(failure).message) })
    return () => { active = false; controller.abort() }
  }, [client, workspaceId, attempt])
  const scoped = useMemo(() => client.forWorkspace(workspaceId), [client, workspaceId])
  useEffect(() => () => scoped.cancelRequests(), [scoped])
  if (error) return <section><h1>Company not available.</h1><RetryError message={error} retry={() => { setError(''); setAttempt(a => a + 1) }} /><Link to="/companies">Choose a company</Link></section>
  if (!company) return <p role="status">Opening company...</p>
  return <CompanyContext value={{ lifecycleEnabled, role: company.role }}><div className="company-switchbar"><strong>{company.name}</strong><span>{company.role}</span> <Link to="/companies">Switch company</Link><span>Open forms are cleared when switching.</span></div>
    {lifecycleEnabled && <nav className="company-tabs" aria-label="Company navigation"><Link to={'/companies/' + workspaceId + '/dashboard'}>Overview</Link><Link to={'/companies/' + workspaceId + '/projects'}>Projects</Link><Link to={'/companies/' + workspaceId + '/team'}>Team & access</Link>{(company.role === 'Owner' || company.role === 'Admin') && <Link to={'/companies/' + workspaceId + '/audit'}>Company audit</Link>}</nav>}<Routes>
    {lifecycleEnabled && <><Route path="team" element={<CompanyTeamPage client={scoped} company={company} refreshCompany={() => setAttempt(a => a + 1)} />} /><Route path="audit" element={<CompanyAuditPage client={scoped} />} /></>}
    <Route path="dashboard" element={<DashboardPage client={scoped} />} />
    <Route path="projects" element={<ProjectsPage client={scoped} />} />
    <Route path="projects/new" element={company.role === "Guest" ? <section><h1>Guests cannot create projects.</h1><Link to={"/companies/" + workspaceId + "/projects"}>Back to projects</Link></section> : <NewProjectPage client={scoped} />} />
    <Route path="projects/:projectId" element={<ProjectDetailPage client={scoped} />} />
    <Route path="projects/:projectId/edit" element={<EditProjectPage client={scoped} />} />
    <Route path="projects/:projectId/tasks" element={<TasksPage client={scoped} />} />
    <Route path="projects/:projectId/tasks/new" element={<NewTaskPage client={scoped} />} />
    <Route path="projects/:projectId/activity" element={<ActivityPage client={scoped} />} />
    <Route path="tasks/:taskId" element={<TaskDetailPage client={scoped} />} />
    <Route path="tasks/:taskId/edit" element={<EditTaskPage client={scoped} />} />
    <Route path="activity" element={<ActivityHub client={scoped} />} />
    <Route path="*" element={<section><h1>That company page is not here.</h1><Link to={'/companies/' + workspaceId + '/dashboard'}>Company overview</Link></section>} />
  </Routes></CompanyContext>
}
