import { useEffect, useRef, useState } from 'react'
import type { ApiClient } from '../../api/client'
import { ApiError, asApiError } from '../../api/client'
import type { TaskItem } from '../tasks/types'
import type { BoardStatus } from './board-api'
import { placement, readPlacement, statuses, statusLabel } from './board-state'
import type { Placement } from './board-state'

export type MoveIntent = { task: TaskItem; status: BoardStatus; before?: string; step?: -1 | 1 }
export function MoveTaskDialog({ client, projectId, intent, pending, failure, onCancel, onReload, onUnavailable, onCommit }: {
  client: ApiClient; projectId: string; intent: MoveIntent; pending: boolean; failure?: ApiError
  onCancel: () => void; onReload: () => void; onUnavailable: () => void; onCommit: (target: Placement) => Promise<void>
}) {
  const dialog = useRef<HTMLDialogElement>(null), select = useRef<HTMLSelectElement>(null), review = useRef<HTMLButtonElement>(null), saving = useRef<HTMLParagraphElement>(null)
  const [status, setStatus] = useState(intent.status), [attempt, setAttempt] = useState(0)
  const [state, setState] = useState<{ status: BoardStatus; items: TaskItem[]; error?: string; loading: boolean }>({ status, items: [], loading: true })
  const [position, setPosition] = useState('end')
  useEffect(() => {
    const node = dialog.current
    if (node && !node.open) { if (typeof node.showModal === 'function') node.showModal(); else node.setAttribute('open', '') }
    select.current?.focus()
    return () => { if (node?.open && typeof node.close === 'function') node.close() }
  }, [])
  useEffect(() => {
    const controller = new AbortController(); let active = true
    queueMicrotask(() => {
      if (!active) return
      setState({ status, items: [], loading: true })
      void readPlacement(client, projectId, status, intent.task.id, controller.signal).then(({ items, sourceIndex }) => {
        if (!active) return
        let position = 'end'
        if (status === intent.status && intent.before) {
          if (!items.some(t => t.id === intent.before)) throw new ApiError('The drop position changed. Reload the board and choose a current position.')
          position = intent.before
        }
        if (status === intent.task.status && intent.step) {
          if (sourceIndex < 0) throw new ApiError('The task changed columns. Reload the board before choosing a position.')
          const index = Math.max(0, Math.min(items.length, sourceIndex + intent.step))
          position = items[index]?.id ?? 'end'
        }
        setPosition(position); setState({ status, items, loading: false })
      }).catch(error => {
        if (!active) return
        const problem = asApiError(error)
        if (problem.status === 404 || problem.status === 401) onUnavailable()
        else setState({ status, items: [], loading: false, error: problem.message })
      })
    })
    return () => { active = false; controller.abort() }
  }, [client, projectId, status, attempt, intent, onUnavailable])
  useEffect(() => { if (pending) saving.current?.focus() }, [pending])
  useEffect(() => { if (failure) review.current?.focus() }, [failure])
  const ready = !state.loading && state.status === status && !state.error && !failure && !pending
  const index = position === 'start' ? 0 : position === 'end' ? state.items.length : state.items.findIndex(t => t.id === position)
  function containTab(event: React.KeyboardEvent<HTMLDialogElement>) {
    if (event.key !== 'Tab') return
    const controls = [...event.currentTarget.querySelectorAll<HTMLElement>('button:not(:disabled),select:not(:disabled),a[href],input:not(:disabled)')]
    if (!controls.length) { event.preventDefault(); saving.current?.focus(); return }
    const first = controls[0], last = controls[controls.length - 1]
    if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus() }
    else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus() }
  }
  return <dialog ref={dialog} className="board-move-dialog" aria-labelledby="move-task-title" aria-describedby="move-task-help" onKeyDown={containTab} onCancel={event => { event.preventDefault(); if (!pending) onCancel() }}>
    <form onSubmit={event => { event.preventDefault(); if (ready && index >= 0) void onCommit(placement(status, state.items, index)) }} aria-busy={pending}>
      <p className="eyebrow">Move task</p><h2 id="move-task-title">Move {intent.task.title}</h2>
      <p id="move-task-help">Choose a column and position, then confirm. Other changes may require you to reload and choose again.</p>
      <fieldset disabled={pending || !!failure}>
        <label htmlFor="move-target-status">Destination column</label><select ref={select} id="move-target-status" value={status} onChange={event => setStatus(event.target.value as BoardStatus)}>{statuses.map(s => <option key={s} value={s}>{statusLabel[s]}</option>)}</select>
        <label htmlFor="move-target-position">Position in column</label><select id="move-target-position" value={position} disabled={!ready} onChange={event => setPosition(event.target.value)}><option value="start">At the start</option><option value="end">At the end</option>{state.items.map(t => <option key={t.id} value={t.id}>Before {t.title}</option>)}</select>
      </fieldset>
      {pending && <p ref={saving} tabIndex={-1} role="status">Saving move. Please wait for confirmation.</p>}
      {state.loading && <p role="status">Loading current positions...</p>}
      {state.error && <div role="alert"><p>{state.error}</p><button type="button" className="button subtle" onClick={() => setAttempt(a => a + 1)}>Reload positions</button></div>}
      {failure && <div role="alert"><p>{failure.message}</p><p>Your move was not retried. Reload the board and review the position before trying again.</p><button ref={review} type="button" className="button subtle" onClick={onReload}>Reload board to review</button></div>}
      <div className="form-actions"><button type="button" className="button subtle" disabled={pending} onClick={onCancel}>Cancel move</button><button className="button primary" disabled={!ready || index < 0}>{pending ? 'Saving move...' : 'Confirm move'}</button></div>
    </form>
  </dialog>
}
