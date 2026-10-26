import { RetryError } from '../../components/Feedback'
import { Link } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { Badge, Icon } from '../../components/Workspace'
import { useApiResource } from '../../hooks/use-api-resource'
import type { ProjectPage } from '../projects/types'
export function ActivityHub({ client }: { client: ApiClient }) {
  const { result, retry } = useApiResource<ProjectPage>(client, '/projects?pageSize=100&sortBy=updatedAtUtc')
  return <section className="projects-page"><div className="page-heading"><div><p className="eyebrow">Visibility & accountability</p><h1>Activity center</h1><p className="muted">Follow project decisions, task changes and team access in one place.</p></div><Badge value="readonly">Read-only history</Badge></div>
    <div className="audit-explainer"><Icon name="activity" size={28} /><div><h2>A clear record of what changed</h2><p>Choose a project to review its timeline. Each record preserves the actor, action and timestamp. History stays available after individual tasks are deleted.</p></div></div>
    {result.status === 'loading' && <p role="status">Loading project histories...</p>}
    {result.status === 'error' && <RetryError message={result.error.message} retry={retry} label="Reload projects" />}
    {result.status === 'ready' && <><ul className="project-grid">{result.data.items.map(project => <li className="project-card" key={project.id}><span className="project-symbol"><Icon name="activity" /></span><h2><Link to={'/projects/' + project.id + '/activity'}>{project.name}</Link></h2><p className="project-excerpt">Review task updates, membership changes and project decisions.</p><Link className="text-link" to={'/projects/' + project.id + '/activity'}>Open timeline <span aria-hidden="true">&#8599;</span></Link></li>)}</ul>{result.data.items.length === 0 && <div className="empty-workspace"><h2>No project history to review yet.</h2><Link className="button primary" to="/projects/new">Create a project</Link></div>}{result.data.totalCount > result.data.items.length && <p className="notice">Showing the first {result.data.items.length} project histories. <Link to="/projects">Search all projects</Link> to open another timeline.</p>}</>}
  </section>
}
