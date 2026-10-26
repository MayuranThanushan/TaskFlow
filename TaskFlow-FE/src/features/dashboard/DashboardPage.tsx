import { RetryError } from '../../components/Feedback'
import type { components } from '../../api/generated'
import { useState } from 'react'
import { Badge, Icon } from '../../components/Workspace'
import type { ProjectPage } from '../projects/types'
import type { TaskPage } from '../tasks/types'
import { Link } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { useAuth } from '../auth/auth-context'
import { useApiResource } from '../../hooks/use-api-resource'

export type DashboardTotals = components['schemas']['DashboardDto']
export function DashboardPage({ client }: { client: ApiClient }) {
  const { session } = useAuth()
  const { result, retry } = useApiResource<DashboardTotals>(client, '/dashboard')
  return <section className="dashboard">
    <div className="overview-heading"><div><p className="eyebrow">Workspace overview</p><h1>Welcome back{session.user ? `, ${session.user.displayName}` : ''}.</h1>
    <p className="intro">Everything you need to keep work moving.</p></div><Link className="button primary" to="/projects/new"><Icon name="plus" />New project</Link></div><p className="account-reference">Your account ID: {session.user?.id}</p>
    <div className="form-actions dashboard-actions"><Link className="button primary" to="/projects">Browse projects</Link><button className="button subtle" disabled={result.status === 'loading'} onClick={retry}>Refresh overview</button></div>
    {result.status === 'loading' && <p className="project-state" role="status">Loading your overview...</p>}
    {result.status === 'error' && <RetryError message={result.error.message} retry={retry} />}
    {result.status === 'ready' && <>
      <section aria-labelledby="workspace-totals"><h2 id="workspace-totals">Workspace totals</h2><dl className="dashboard-stats">
        <div><dt>Projects</dt><dd>{result.data.totalProjects}</dd></div><div><dt>Tasks</dt><dd>{result.data.totalTasks}</dd></div><div><dt>Assigned to me</dt><dd>{result.data.assignedToMe}</dd></div><div><dt>Overdue tasks</dt><dd>{result.data.overdueTasks}</dd></div>
      </dl><p className="field-hint">Overdue tasks have a past deadline and are not completed. Totals include projects you own or belong to.</p></section>
      <div className="overview-columns"><section className="status-panel" aria-labelledby="task-status-totals"><div className="section-title"><h2 id="task-status-totals">Tasks by status</h2><span>Current workload</span></div><div className="status-distribution" aria-hidden="true">{Object.entries(result.data.tasksByStatus).filter(([, count]) => count > 0).map(([status, count]) => <span key={status} className={status} style={{ flex: count }} />)}</div><dl className="dashboard-stats status-stats"><div><dt>To Do</dt><dd>{result.data.tasksByStatus.toDo}</dd></div><div><dt>In Progress</dt><dd>{result.data.tasksByStatus.inProgress}</dd></div><div><dt>Completed</dt><dd>{result.data.tasksByStatus.done}</dd></div></dl></section><section className="health-panel"><p className="eyebrow">Progress at a glance</p><strong className="health-value">{result.data.totalTasks ? Math.round(result.data.tasksByStatus.done / result.data.totalTasks * 100) : 0}<span>%</span></strong><p>of active tasks completed</p><progress aria-label="Workspace task completion" max={Math.max(result.data.totalTasks, 1)} value={result.data.tasksByStatus.done} /><p className={result.data.overdueTasks ? 'overdue-label' : 'muted'}>{result.data.overdueTasks ? `${result.data.overdueTasks} overdue ${result.data.overdueTasks === 1 ? 'task needs' : 'tasks need'} attention` : 'No overdue tasks. You\u2019re on track.'}</p></section></div>
      {result.data.totalProjects > 0 && <RecentWork key={session.user?.id} client={client} />}
      {result.data.totalProjects === 0 ? <section className="empty-workspace" aria-labelledby="workspace-title"><span className="empty-icon" aria-hidden="true">+</span><h2 id="workspace-title">Room for your next idea.</h2><p>Give your next idea a place to grow.</p><Link className="button primary" to="/projects/new">Create a project</Link></section>
        : result.data.totalTasks === 0 && <p className="notice" role="status">Your projects are ready. Add a task to start tracking progress.</p>}
    </>}
  </section>
}

function RecentWork({ client }: { client: ApiClient }) {
  const projects = useApiResource<ProjectPage>(client, '/projects?pageSize=4&sortBy=updatedAtUtc&sortDirection=desc')
  const [selected, setSelected] = useState('')
  const items = projects.result.status === 'ready' ? projects.result.data.items : []
  const current = items.some(project => project.id === selected) ? selected : items[0]?.id
  return <section className="recent-work"><div className="section-title"><div><p className="eyebrow">Pick up where you left off</p><h2>Recent projects</h2></div><Link className="text-link" to="/projects">View all projects <span aria-hidden="true">&#8599;</span></Link></div>
    {projects.result.status === 'loading' && <p role="status">Loading recent projects...</p>}
    {projects.result.status === 'error' && <div className="notice"><p>Recent projects could not be loaded.</p><button className="text-button" onClick={projects.retry}>Reload recent projects</button></div>}
    <div className="recent-project-grid">{items.map(project => <article className={'recent-project' + (current === project.id ? ' selected' : '')} key={project.id}><span className="project-symbol"><Icon name="projects" /></span><h3><Link to={'/projects/' + project.id}>{project.name}</Link></h3><p>{project.description || 'Your next steps start here.'}</p><div className="card-links"><button className="text-button" aria-pressed={current === project.id} onClick={() => setSelected(project.id)}>Preview tasks</button><Link to={'/projects/' + project.id + '/activity'}>Activity</Link></div></article>)}</div>
    {current && <TaskPreview key={current} client={client} projectId={current} />}
  </section>
}
function TaskPreview({ client, projectId }: { client: ApiClient; projectId: string }) {
  const { result, retry } = useApiResource<TaskPage>(client, '/projects/' + projectId + '/tasks?pageSize=5&sortBy=createdAtUtc')
  return <section className="task-preview"><div className="section-title"><h3>Latest tasks</h3><Link className="text-link" to={'/projects/' + projectId + '/tasks'}>View task list</Link></div>
    {result.status === 'loading' && <p role="status">Loading latest tasks...</p>}
    {result.status === 'error' && <button className="button subtle" onClick={retry}>Reload latest tasks</button>}
    {result.status === 'ready' && (result.data.items.length ? <ul className="preview-tasks">{result.data.items.map(task => <li key={task.id}><span className={'task-check ' + (task.status === 'Done' ? 'checked' : '')} aria-hidden="true">{task.status === 'Done' ? '\u2713' : ''}</span><Link to={'/tasks/' + task.id}>{task.title}</Link><Badge value={task.priority} /><Badge value={task.status}>{task.status === 'InProgress' ? 'In progress' : task.status === 'ToDo' ? 'To do' : 'Done'}</Badge></li>)}</ul> : <p className="muted">No tasks in this project yet. <Link to={'/projects/' + projectId + '/tasks/new'}>Add the first task</Link>.</p>)}
  </section>
}
