import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useNavigate } from 'react-router-dom'
import { describe, expect, it, vi } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'
import type { TaskItem } from '../tasks/types'

const item = (id: string, title: string, status: TaskItem['status'] = 'ToDo'): TaskItem => ({ id, projectId: '41', title, description: null, status, priority: 'Medium', assigneeId: null, dueDateUtc: null, createdAtUtc: '2026-11-15T08:00:00Z', updatedAtUtc: '2026-11-15T08:00:00Z', version: '1' })
function SwitchCompany() { const navigate = useNavigate(); return <button onClick={() => void navigate('/companies/2/projects/41/board')}>Switch test company</button> }
function setup(options: { extra?: number; role?: string; initial?: string } = {}) {
  const auth = server(true)
  const state = { tasks: [item('101', 'Plan release'), item('102', 'Review API', 'InProgress'), item('103', 'Ship notes', 'Done')], role: options.role ?? 'Owner', readStatus: 200, moveStatus: 200, moveCode: 'board.position_conflict', wait: null as Promise<void> | null, readWait: null as Promise<void> | null, writes: [] as { body: Record<string, string | null>; version: unknown }[], calls: [] as { url: string; base: string; page: number; size: number }[], failRefresh: false }
  for (let n = 0; n < (options.extra ?? 0); n++) state.tasks.push(item(String(200 + n), 'Extra ' + n))
  const client = createApiClient(async c => {
    if (c.url?.startsWith('/auth/')) return auth.adapter(c)
    if (c.url === '/workspaces/1' || c.url === '/workspaces/2') return reply(c, 200, { id: c.url.endsWith('1') ? '1' : '2', name: 'Studio', role: state.role })
    const url = new URL(c.url!, 'https://example.test'), page = Number(c.params?.page ?? url.searchParams.get('page') ?? 1), size = Number(c.params?.pageSize ?? url.searchParams.get('pageSize') ?? 20)
    state.calls.push({ url: url.pathname, base: c.baseURL!, page, size })
    const tasks = c.baseURL?.endsWith('/2') ? [item('901', 'Other company card')] : state.tasks
    const column = (status: string) => tasks.filter(t => t.status === status)
    if (url.pathname.endsWith('/move')) {
      const body = JSON.parse(c.data); state.writes.push({ body, version: c.headers.get('X-Expected-Version') }); await state.wait
      if (state.moveStatus !== 200) return reply(c, state.moveStatus, { code: state.moveCode })
      const id = url.pathname.split('/').at(-2)!, task = state.tasks.find(t => t.id === id)!
      const target = column(body.status).filter(t => t.id !== id), at = body.nextTaskId ? target.findIndex(t => t.id === body.nextTaskId) : target.length
      expect(target[at - 1]?.id ?? null).toBe(body.previousTaskId)
      expect(target[at]?.id ?? null).toBe(body.nextTaskId)
      expect(task.version).toBe(c.headers.get('X-Expected-Version'))
      state.tasks = state.tasks.filter(t => t.id !== id)
      task.status = body.status; task.version = String(Number(task.version) + 1)
      const nextIndex = body.nextTaskId ? state.tasks.findIndex(t => t.id === body.nextTaskId) : -1
      if (nextIndex < 0) state.tasks.push(task); else state.tasks.splice(nextIndex, 0, task)
      if (state.failRefresh) state.readStatus = 503
      return reply(c, 200, { ...task })
    }
    await state.readWait
    if (state.readStatus !== 200) return reply(c, state.readStatus, {})
    if (url.pathname === '/projects/41') return reply(c, 200, { id: '41', name: 'Launch workspace', description: null, ownerId: profile.id, version: '1', createdAtUtc: '2026-11-15T08:00:00Z', updatedAtUtc: '2026-11-15T08:00:00Z', totalTasks: tasks.length, completedTasks: column('Done').length, progressPercentage: Math.round(column('Done').length * 100 / tasks.length) })
    if (url.pathname.endsWith('/members')) return reply(c, 200, { items: [{ userId: profile.id, displayName: profile.displayName, role: state.role }] })
    if (url.pathname.endsWith('/board')) return reply(c, 200, { projectId: '41', totalCount: tasks.length, columns: ['ToDo', 'InProgress', 'Done'].map(status => ({ status, totalCount: column(status).length })) })
    if (url.pathname.includes('/columns/')) {
      const rows = column(url.pathname.split('/').at(-1)!)
      return reply(c, 200, { items: rows.slice((page - 1) * size, page * size).map(t => ({ ...t })), totalCount: rows.length, totalPages: Math.ceil(rows.length / size), page, pageSize: size })
    }
    if (url.pathname.endsWith('/tasks')) { const rows = tasks; return reply(c, 200, { items: rows, page: 1, pageSize: 20, totalCount: rows.length, totalPages: 1 }) }
    return reply(c, 404, {})
  })
  const rendered = render(<MemoryRouter initialEntries={[options.initial ?? '/companies/1/projects/41/board']}><SwitchCompany /><App client={client} companiesEnabled lifecycleEnabled /></MemoryRouter>)
  return { state, client, ...rendered }
}
async function loaded() { await screen.findByRole('link', { name: 'Plan release' }); await waitFor(() => expect(screen.getByRole('button', { name: 'Reload board' })).not.toBeDisabled()) }
async function moveDialog(title = 'Plan release') { await userEvent.click(screen.getByRole('button', { name: 'Move ' + title })); return screen.findByRole('dialog') }
async function destination(status: string) { await userEvent.selectOptions(screen.getByLabelText('Destination column'), status); await waitFor(() => expect(screen.getByLabelText('Position in column')).not.toBeDisabled()) }

describe('company board UI', () => {
  it('loads three canonical columns, truthful counts/progress and scoped list links', async () => {
    setup(); await loaded()
    expect(screen.getByRole('heading', { name: 'Project board' })).toBeInTheDocument()
    for (const name of ['To do', 'In progress', 'Completed']) expect(screen.getByRole('region', { name })).toBeInTheDocument()
    expect(screen.getByRole('progressbar', { name: 'Project completion' })).toHaveAttribute('value', '1')
    expect(screen.getByRole('link', { name: 'List view' })).toHaveAttribute('href', '/companies/1/projects/41/tasks')
  })
  it('paginates one column beyond 100 cards without truncating its total or changing other pages', async () => {
    const { state } = setup({ extra: 120 }); await loaded()
    const column = screen.getByRole('region', { name: 'To do' })
    expect(within(column).getByText('20 shown of 121')).toBeInTheDocument()
    await userEvent.click(within(column).getByRole('button', { name: 'Next' }))
    await screen.findByRole('link', { name: 'Extra 19' })
    expect(state.calls.some(c => c.url.endsWith('/columns/ToDo') && c.page === 2)).toBe(true)
    expect(state.calls.filter(c => c.url.endsWith('/columns/Done')).every(c => c.page === 1)).toBe(true)
  })
  it('provides readable cards but no move/create controls to viewers', async () => {
    setup({ role: 'Viewer' }); await loaded()
    expect(screen.queryByRole('button', { name: 'Move Plan release' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'New task' })).not.toBeInTheDocument()
    expect(screen.getByText(/Read-only access/)).toBeInTheDocument()
  })
  it('moves by click, sends captured revisions once, refreshes counts/progress and restores focus', async () => {
    const { state } = setup(); await loaded(); await moveDialog(); await destination('Done')
    await userEvent.dblClick(screen.getByRole('button', { name: 'Confirm move' }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(screen.getByRole('button', { name: 'Move Plan release' })).toHaveFocus())
    expect(state.writes).toHaveLength(1)
    expect(state.writes[0]).toEqual({ body: { status: 'Done', previousTaskId: '103', previousVersion: '1', nextTaskId: null, nextVersion: null }, version: '1' })
    expect(within(screen.getByRole('region', { name: 'Completed' })).getByRole('link', { name: 'Plan release' })).toBeInTheDocument()
    expect(screen.getByRole('progressbar')).toHaveAttribute('value', '2')
  })
  it('prepares an exact one-place keyboard reorder and supports Escape cancellation with focus return', async () => {
    const { state } = setup({ extra: 2 }); await loaded()
    const handle = screen.getByRole('button', { name: 'Move Plan release' }); handle.focus(); fireEvent.keyDown(handle, { key: 'ArrowDown', altKey: true })
    await screen.findByRole('dialog'); await waitFor(() => expect(screen.getByLabelText('Position in column')).not.toBeDisabled())
    await userEvent.click(screen.getByRole('button', { name: 'Confirm move' }))
    await waitFor(() => expect(state.writes).toHaveLength(1)); expect(state.writes[0].body.previousTaskId).toBe('200'); expect(state.writes[0].body.nextTaskId).toBe('201')
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument()); await waitFor(() => expect(handle).toHaveFocus())
    const dialog = await moveDialog(); fireEvent(dialog, new Event('cancel', { bubbles: true, cancelable: true }))
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument()); expect(state.writes).toHaveLength(1)
  })
  it('resolves drag-before at a later page boundary using the real off-page previous neighbor', async () => {
    const { state } = setup({ extra: 25 }); await loaded()
    await userEvent.click(within(screen.getByRole('region', { name: 'To do' })).getByRole('button', { name: 'Next' })); const card = await screen.findByRole('link', { name: 'Extra 19' })
    const transfer = { setData: vi.fn(), effectAllowed: '', dropEffect: '' }
    fireEvent.dragStart(screen.getByRole('button', { name: 'Move Review API' }), { dataTransfer: transfer }); fireEvent.drop(card.closest('li')!, { dataTransfer: transfer })
    await screen.findByRole('dialog'); await waitFor(() => expect(screen.getByLabelText('Position in column')).toHaveValue('219'))
    await userEvent.click(screen.getByRole('button', { name: 'Confirm move' })); await waitFor(() => expect(state.writes).toHaveLength(1))
    expect(state.writes[0].body.previousTaskId).toBe('218'); expect(state.writes[0].body.nextTaskId).toBe('219')
  })
  it('rolls back optimistic failure, retains review controls and never retries a stale move automatically', async () => {
    const { state } = setup(); await loaded(); await moveDialog(); await destination('Done')
    let release!: () => void; state.wait = new Promise(resolve => { release = resolve }); state.moveStatus = 409
    await userEvent.click(screen.getByRole('button', { name: 'Confirm move' }))
    expect(within(screen.getByRole('region', { name: 'Completed' })).getByRole('link', { name: 'Plan release' })).toBeInTheDocument()
    await act(async () => release()); await screen.findByRole('button', { name: 'Reload board to review' })
    expect(within(screen.getByRole('region', { name: 'To do' })).getByRole('link', { name: 'Plan release' })).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Confirm move' })).toBeDisabled()
    await userEvent.click(screen.getByRole('button', { name: 'Reload board to review' })); await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument())
    await waitFor(() => expect(screen.getByRole('button', { name: 'Reload board' })).not.toBeDisabled()); expect(state.writes).toHaveLength(1)
  })
  it('distinguishes a saved move from a failed post-commit refresh and blocks further moves', async () => {
    const { state } = setup(); await loaded(); state.failRefresh = true; await moveDialog(); await destination('Done'); await userEvent.click(screen.getByRole('button', { name: 'Confirm move' }))
    await screen.findByRole('heading', { name: 'The board could not be refreshed.' }); expect(screen.getByText(/The move was saved, but the board/)).toBeInTheDocument(); expect(state.tasks.find(t => t.id === '101')?.status).toBe('Done'); expect(state.writes).toHaveLength(1)
    expect(screen.queryByRole('button', { name: 'Move Plan release' })).not.toBeInTheDocument()
  })
  it('clears private board content if access is revoked during a move', async () => {
    const { state } = setup(); await loaded(); state.moveStatus = 404; await moveDialog(); await destination('Done'); await userEvent.click(screen.getByRole('button', { name: 'Confirm move' }))
    await screen.findByRole('heading', { name: 'Board not available.' }); expect(screen.queryByText('Plan release')).not.toBeInTheDocument(); expect(screen.queryByRole('dialog')).not.toBeInTheDocument()
  })
  it('does not render late company-one reads after switching companies', async () => {
    const { state } = setup(); await loaded(); let release!: () => void; state.readWait = new Promise(resolve => { release = resolve })
    await userEvent.click(screen.getByRole('button', { name: 'Reload board' })); await userEvent.click(screen.getByRole('button', { name: 'Switch test company' }))
    state.readWait = null; await act(async () => release()); await screen.findByRole('link', { name: 'Other company card' }); expect(screen.queryByRole('link', { name: 'Plan release' })).not.toBeInTheDocument()
  })
})
