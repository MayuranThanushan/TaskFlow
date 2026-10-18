import { Link } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { useAuth } from '../auth/auth-context'
import { useProjectResource } from '../projects/use-project-resource'

export interface DashboardTotals {
  totalProjects: number; totalTasks: number; tasksByStatus: { toDo: number; inProgress: number; done: number }; assignedToMe: number; overdueTasks: number
}
export function DashboardPage({ client }: { client: ApiClient }) {
  const { session } = useAuth()
  const { result, retry } = useProjectResource<DashboardTotals>(client, '/dashboard')
  return <section className="dashboard">
    <p className="eyebrow">Your workspace</p><h1>Welcome back{session.user ? `, ${session.user.displayName}` : ''}.</h1>
    <p className="intro">A clear view of your projects and tasks.</p><p className="muted">Your account ID: {session.user?.id}</p>
    <div className="form-actions dashboard-actions"><Link className="button primary" to="/projects">Browse projects</Link><button className="button subtle" disabled={result.status === 'loading'} onClick={retry}>Refresh overview</button></div>
    {result.status === 'loading' && <p className="project-state" role="status">Loading your overview...</p>}
    {result.status === 'error' && <div className="error-summary" role="alert"><p>{result.error.message}</p><button className="button subtle" onClick={retry}>Try again</button></div>}
    {result.status === 'ready' && <>
      <section aria-labelledby="workspace-totals"><h2 id="workspace-totals">Workspace totals</h2><dl className="dashboard-stats">
        <div><dt>Projects</dt><dd>{result.data.totalProjects}</dd></div><div><dt>Tasks</dt><dd>{result.data.totalTasks}</dd></div><div><dt>Assigned to me</dt><dd>{result.data.assignedToMe}</dd></div><div><dt>Overdue tasks</dt><dd>{result.data.overdueTasks}</dd></div>
      </dl><p className="field-hint">Overdue tasks have a past deadline and are not completed. Totals include projects you own or belong to.</p></section>
      <section aria-labelledby="task-status-totals"><h2 id="task-status-totals">Tasks by status</h2><dl className="dashboard-stats status-stats"><div><dt>To Do</dt><dd>{result.data.tasksByStatus.toDo}</dd></div><div><dt>In Progress</dt><dd>{result.data.tasksByStatus.inProgress}</dd></div><div><dt>Completed</dt><dd>{result.data.tasksByStatus.done}</dd></div></dl></section>
      {result.data.totalProjects === 0 ? <section className="empty-workspace" aria-labelledby="workspace-title"><span className="empty-icon" aria-hidden="true">+</span><h2 id="workspace-title">Room for your next idea.</h2><p>Give your next idea a place to grow.</p><Link className="button primary" to="/projects/new">Create a project</Link></section>
        : result.data.totalTasks === 0 && <p className="notice" role="status">Your projects are ready. Add a task to start tracking progress.</p>}
    </>}
  </section>
}
