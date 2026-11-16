import { SavedViews } from '../views/SavedViews'
import { useCallback, useEffect, useRef, useState } from 'react'
import type { DragEvent, KeyboardEvent } from 'react'
import { ApiError, asApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import { Pagination } from '../../components/Feedback'
import { Badge, Icon } from '../../components/Workspace'
import { useAuth } from '../auth/auth-context'
import { Link, useParams } from '../companies/navigation'
import type { TaskItem } from '../tasks/types'
import { moveBoardTask } from './board-api'
import type { BoardStatus } from './board-api'
import { optimisticMove, pageSize, readBoard, statuses, statusLabel } from './board-state'
import type { BoardSnapshot, Pages, Placement } from './board-state'
import { MoveTaskDialog } from './MoveTaskDialog'
import type { MoveIntent } from './MoveTaskDialog'
import './board.css'

export function BoardPage({ client }: { client: ApiClient }) {
  const { projectId = '' } = useParams(), { session } = useAuth()
  return <BoardContent key={client.resourceKey + ':' + projectId + ':' + session.user?.id} client={client} projectId={projectId} identity={session.user?.id ?? ''} />
}
function BoardContent({ client, projectId, identity }: { client: ApiClient; projectId: string; identity: string }) {
  const [pages, setPages] = useState<Pages>({ ToDo: 1, InProgress: 1, Done: 1 }), [revision, setRevision] = useState(0)
  const [data, setData] = useState<BoardSnapshot>(), [reading, setReading] = useState(true), [loadError, setLoadError] = useState<ApiError>()
  const [editor, setEditor] = useState<MoveIntent>(), [pending, setPending] = useState(false), [failure, setFailure] = useState<ApiError>()
  const [committed, setCommitted] = useState(false)
  const [blocked, setBlocked] = useState(false), [notice, setNotice] = useState(''), [announcement, setAnnouncement] = useState('')
  const [focusTask, setFocusTask] = useState<string>(), [dragColumn, setDragColumn] = useState<BoardStatus>()
  const dragged = useRef<TaskItem | undefined>(undefined), writing = useRef(false), alive = useRef(false), writeAbort = useRef<AbortController | undefined>(undefined)
  const heading = useRef<HTMLHeadingElement>(null)
  useEffect(() => { alive.current = true; return () => { alive.current = false; writeAbort.current?.abort() } }, [])
  useEffect(() => {
    const controller = new AbortController(); let active = true
    queueMicrotask(() => {
      if (!active) return
      setReading(true); setLoadError(undefined)
      void readBoard(client, projectId, pages, controller.signal).then(snapshot => {
        if (active) { setData(snapshot); setReading(false); setBlocked(false); setNotice(text => text.replace('Refreshing the board.', 'Board refreshed.')); setAnnouncement(text => text.replace('Refreshing the board.', 'Board refreshed.')) }
      }).catch(error => {
        if (!active) return
        const problem = asApiError(error)
        controller.abort(); setData(undefined); setEditor(undefined); setReading(false); setLoadError(problem); setBlocked(true); setFocusTask('heading')
        if (problem.status === 404 || problem.status === 401 || problem.status === 403) { setNotice(''); setAnnouncement('') }
      })
    })
    return () => { active = false; controller.abort() }
  }, [client, projectId, pages, revision])
  useEffect(() => {
    if (editor || reading || !focusTask) return
    const handle = document.getElementById('board-move-' + focusTask)
    const target = (handle && !handle.matches(':disabled') ? handle : null) ?? document.getElementById('board-task-' + focusTask) ?? heading.current
    target?.focus()
  }, [editor, reading, focusTask, data])
  const unavailable = useCallback(() => { setData(undefined); setEditor(undefined); setBlocked(true); setLoadError(new ApiError('This board is no longer available.', 404)); setNotice(''); setAnnouncement(''); setFocusTask('heading') }, [])
  const path = '/projects/' + encodeURIComponent(projectId)
  const canWrite = !!data?.members.items.some(m => m.userId === identity && ['Owner', 'Editor', 'Member'].includes(m.role))
  const busy = pending || reading || blocked
  function open(intent: MoveIntent) {
    if (!canWrite || busy || writing.current) return
    setFailure(undefined); setFocusTask(undefined); setEditor(intent)
  }
  function cancel() { if (!writing.current) { setFocusTask(editor?.task.id); setEditor(undefined) } }
  function reload() { if (writing.current) return; setEditor(undefined); setFailure(undefined); setBlocked(true); setFocusTask(editor?.task.id); setRevision(r => r + 1) }
  async function commit(target: Placement) {
    if (!data || !editor || !canWrite || busy || writing.current) return
    const before = data, task = editor.task
    const optimistic = optimisticMove(before, task, target)
    writing.current = true; setCommitted(false); setPending(true); setFailure(undefined); setData(optimistic)
    const controller = new AbortController(); writeAbort.current = controller
    try {
      await moveBoardTask(client, projectId, task.id, task.version, target.move, controller.signal)
      if (!alive.current) return
      const message = task.title + ' saved in ' + statusLabel[target.move.status] + '. Refreshing the board.'
      setCommitted(true); setNotice(message); setAnnouncement(message); setEditor(undefined); setFocusTask(task.id); setBlocked(true)
      const sourcePage = Math.max(1, Math.min(pages[task.status], optimistic.columns[task.status].totalPages))
      setPages({ ...pages, [task.status]: sourcePage, [target.move.status]: Math.floor(target.index / pageSize) + 1 })
      setRevision(r => r + 1)
    } catch (error) {
      if (!alive.current) return
      const problem = asApiError(error)
      setData(before); setFailure(problem); setBlocked(true); setAnnouncement('Move could not be confirmed. The displayed change was rolled back. Reload before trying again.')
      if (problem.status === 404 || problem.status === 401) { setData(undefined); setEditor(undefined); setLoadError(problem); setNotice(''); setAnnouncement(''); setFocusTask('heading') }
      else setNotice('Move not confirmed. Your displayed board was restored. Reload and choose the position again; no move is automatically retried.')
    } finally { writing.current = false; if (alive.current) setPending(false) }
  }
  function shortcut(event: KeyboardEvent, task: TaskItem) {
    if (!event.altKey || !['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(event.key) || busy) return
    event.preventDefault()
    if (event.key === 'ArrowUp' || event.key === 'ArrowDown') open({ task, status: task.status, step: event.key === 'ArrowUp' ? -1 : 1 })
    else {
      const index = statuses.indexOf(task.status) + (event.key === 'ArrowLeft' ? -1 : 1)
      if (statuses[index]) open({ task, status: statuses[index] })
    }
  }
  function over(event: DragEvent, status: BoardStatus) {
    if (!dragged.current || busy || !canWrite) return
    event.preventDefault(); event.dataTransfer.dropEffect = 'move'; setDragColumn(status)
  }
  function drop(event: DragEvent, status: BoardStatus, before?: string) {
    event.preventDefault(); event.stopPropagation(); setDragColumn(undefined)
    const task = dragged.current; dragged.current = undefined
    if (task && task.id !== before) open({ task, status, before })
  }
  return <section className="projects-page board-page" aria-busy={reading || pending}>
    <Link className="text-link" to={path}>Back to project</Link>
    <SavedViews client={client} surface="board" projectId={projectId} />
    <div className="page-heading"><div><p className="eyebrow">Project work</p><h1 ref={heading} tabIndex={-1}>Project board</h1>{data && <p className="muted board-project-name">{data.project.name}</p>}</div><div className="form-actions"><Link className="button subtle" to={path + '/tasks'}>List view</Link>{canWrite && <Link className="button primary" to={path + '/tasks/new'}>New task</Link>}<button className="button subtle" disabled={pending || reading} onClick={reload}>Reload board</button></div></div>
    <p className="sr-only" role="status" aria-live="polite" aria-atomic="true">{announcement}</p>
    {notice && <p className="notice">{notice}</p>}
    {reading && <p role="status">{data ? 'Refreshing board...' : 'Loading board...'}</p>}
    {loadError && <div className="error-summary" role="alert"><h2>{loadError.status === 404 ? 'Board not available.' : 'The board could not be refreshed.'}</h2><p>{loadError.status === 404 ? 'The project may have been removed, or your access changed.' : committed ? 'The move was saved, but the board could not refresh. Reload before making another change.' : notice ? 'The move could not be confirmed. Reload to verify its outcome before making another change.' : loadError.message}</p><Link to={path + '/tasks'}>Open task list</Link></div>}
    {data && <>
      <div className="board-overview"><div><span className="muted">Project completion</span><strong>{data.project.progressPercentage}%</strong><progress aria-label="Project completion" max={data.project.totalTasks || 1} value={data.project.completedTasks} /></div><p>{data.summary.totalCount} tasks <span aria-hidden="true">&middot;</span> {data.project.completedTasks} completed</p></div>
      <p id="board-keyboard-help" className="field-hint">{canWrite ? 'Drag a move handle, or use Move to choose a column and position. On a Move button, Alt + arrow keys prepare a move; review and confirm before saving.' : 'Read-only access. Open a card to review its details.'}</p>
      {blocked && !reading && !editor && <div className="error-summary" role="alert">Reload the board before making another move. Your previous move will not be retried.</div>}
      <div className="board-columns">{statuses.map(status => {
        const column = data.columns[status]
        return <section key={status} className={'board-column board-column-' + status + (dragColumn === status ? ' board-drop-active' : '')} aria-labelledby={'board-heading-' + status} onDragOver={event => over(event, status)} onDrop={event => drop(event, status)}>
          <header className="board-column-heading"><h2 id={'board-heading-' + status}>{statusLabel[status]}</h2><span className="board-count" aria-label={column.totalCount + ' tasks in ' + statusLabel[status]}>{column.totalCount}</span></header>
          <ol className="board-cards" aria-label={statusLabel[status] + ' tasks'}>{column.items.map(task => <li key={task.id} className="board-card" data-task-id={task.id} onDragOver={event => over(event, status)} onDrop={event => drop(event, status, task.id)}>
            <div className="board-card-top"><Badge value={task.priority} /><span className="muted">#{task.id}</span></div>
            <h3><Link id={'board-task-' + task.id} draggable={false} to={'/tasks/' + task.id}>{task.title}</Link></h3>
            <p className="board-card-assignee">{data.members.items.find(m => m.userId === task.assigneeId)?.displayName ?? (task.assigneeId ? 'Collaborator' : 'Unassigned')}</p>
            {task.dueDateUtc && <p className="board-card-date">Due <time dateTime={task.dueDateUtc}>{new Date(task.dueDateUtc).toLocaleDateString()}</time></p>}
            {canWrite && <button id={'board-move-' + task.id} type="button" className="button subtle board-move-handle" aria-label={'Move ' + task.title} aria-describedby="board-keyboard-help" disabled={busy} draggable={!busy} onClick={() => open({ task, status: task.status })} onKeyDown={event => shortcut(event, task)} onDragStart={event => { if (busy) { event.preventDefault(); return }; dragged.current = task; event.dataTransfer.effectAllowed = 'move'; event.dataTransfer.setData('application/x-taskflow-task', task.id); setAnnouncement('Moving ' + task.title + '. Drop before a card or into a column to choose a position.') }} onDragEnd={() => { dragged.current = undefined; setDragColumn(undefined) }}>Move <Icon name="arrow" size={15} /></button>}
          </li>)}</ol>
          {column.items.length === 0 && <div className="board-empty"><p>{column.totalCount ? 'No cards on this page.' : 'No tasks in this column.'}</p>{column.totalCount > 0 && <button className="button subtle" disabled={busy} onClick={() => setPages(p => ({ ...p, [status]: 1 }))}>Go to first {statusLabel[status]} page</button>}</div>}
          <div className="board-column-footer"><span className="field-hint">{column.items.length} shown of {column.totalCount}</span><fieldset disabled={busy}><Pagination label={statusLabel[status] + ' column pages'} page={column.page} totalPages={column.totalPages} onPage={page => { setFocusTask(undefined); setPages(p => ({ ...p, [status]: page })) }} /></fieldset></div>
        </section>
      })}</div>
      {editor && <MoveTaskDialog client={client} projectId={projectId} intent={editor} pending={pending} failure={failure} onCancel={cancel} onReload={reload} onUnavailable={unavailable} onCommit={commit} />}
    </>}
  </section>
}
