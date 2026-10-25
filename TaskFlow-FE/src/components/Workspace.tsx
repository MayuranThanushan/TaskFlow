import { Link } from 'react-router-dom'
import type { ApiClient } from '../api/client'
import { useProjectResource } from '../features/projects/use-project-resource'
import type { ProjectPage } from '../features/projects/types'

export function Icon({ name, size = 20 }: { name: string; size?: number }) {
  const shapes: Record<string, string> = {
    overview: 'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
    projects: 'M3 7V5a2 2 0 0 1 2-2h5l2 3h7a2 2 0 0 1 2 2v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z',
    activity: 'M4 12a8 8 0 1 0 2-5 M4 3v5h5 M12 8v5l3 2',
    profile: 'M20 21v-2a6 6 0 0 0-6-6h-4a6 6 0 0 0-6 6v2 M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8',
    tasks: 'M9 6h12 M9 12h12 M9 18h12 M3 6l1 1 2-3 M3 12l1 1 2-3 M3 18l1 1 2-3',
    plus: 'M12 5v14 M5 12h14', arrow: 'M5 12h14 M13 6l6 6-6 6',
  }
  return <svg width={size} height={size} viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true"><path d={shapes[name] ?? shapes.tasks} /></svg>
}
export function Badge({ value, children }: { value: string; children?: React.ReactNode }) {
  return <span className={'badge badge-' + value.toLowerCase()}>{children ?? value}</span>
}
export function ActivityHub({ client }: { client: ApiClient }) {
  const { result, retry } = useProjectResource<ProjectPage>(client, '/projects?pageSize=100&sortBy=updatedAtUtc')
  return <section className="projects-page"><div className="page-heading"><div><p className="eyebrow">Visibility & accountability</p><h1>Activity center</h1><p className="muted">Follow project decisions, task changes and team access in one place.</p></div><Badge value="readonly">Read-only history</Badge></div>
    <div className="audit-explainer"><Icon name="activity" size={28} /><div><h2>A clear record of what changed</h2><p>Choose a project to review its timeline. Each record preserves the actor, action and timestamp. History stays available after individual tasks are deleted.</p></div></div>
    {result.status === 'loading' && <p role="status">Loading project histories...</p>}
    {result.status === 'error' && <div role="alert" className="error-summary"><p>{result.error.message}</p><button className="button subtle" onClick={retry}>Reload projects</button></div>}
    {result.status === 'ready' && <><ul className="project-grid">{result.data.items.map(project => <li className="project-card" key={project.id}><span className="project-symbol"><Icon name="activity" /></span><h2><Link to={'/projects/' + project.id + '/activity'}>{project.name}</Link></h2><p className="project-excerpt">Review task updates, membership changes and project decisions.</p><Link className="text-link" to={'/projects/' + project.id + '/activity'}>Open timeline <span aria-hidden="true">&#8599;</span></Link></li>)}</ul>{result.data.items.length === 0 && <div className="empty-workspace"><h2>No project history to review yet.</h2><Link className="button primary" to="/projects/new">Create a project</Link></div>}{result.data.totalCount > result.data.items.length && <p className="notice">Showing the first {result.data.items.length} project histories. <Link to="/projects">Search all projects</Link> to open another timeline.</p>}</>}
  </section>
}
