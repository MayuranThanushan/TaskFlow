import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'
import type { TaskItem } from './types'
const projectId = '9007199254741111'; const taskId = '9007199254741999'; const memberId = '9007199254741888'
const base = '/projects/' + projectId
const item = (title = 'First task', id = taskId): TaskItem => ({ id, projectId, title, description: null, status: 'ToDo', priority: 'Medium', assigneeId: null, dueDateUtc: null, createdAtUtc: '2026-10-18T08:00:00Z', updatedAtUtc: '2026-10-18T08:00:00Z' })
function Location() { const location = useLocation(); const navigate = useNavigate(); return <><span data-testid="location">{location.pathname + location.search}</span><button onClick={() => void navigate(-1)}>Browser back</button></> }
function setup(path = base + '/tasks', tasks: TaskItem[] = [], authenticated = true) {
  const auth = server(authenticated)
  const state = { tasks, posts: [] as Record<string, unknown>[], wait: null as Promise<void> | null, writes: [] as { path: string; method: string; data: Record<string, unknown> | undefined }[], writeWait: null as Promise<void> | null, writeErrors: { title: ['Please choose a valid title.'] } as Record<string, string[]>, writeStatus: 200, projectOwnerId: profile.id, createStatus: 201, listStatus: 200, detailStatus: 200, projectStatus: 200, currentProfile: { ...profile }, calls: [] as string[] }
  const client = createApiClient(async config => {
    if (config.url === '/auth/me') return reply(config, auth.state.authenticated ? 200 : 401, state.currentProfile)
    if (config.url?.startsWith('/auth/')) return auth.adapter(config)
    const path = new URL(config.url!, 'https://taskflow.test'); state.calls.push(path.pathname)
    if (path.pathname === base) return reply(config, state.projectStatus, { id: projectId, name: 'Task project', description: null, ownerId: state.projectOwnerId, createdAtUtc: '2026-10-18T08:00:00Z', updatedAtUtc: '2026-10-18T08:00:00Z', totalTasks: state.tasks.length, completedTasks: 0, progressPercentage: 0 })
    if (path.pathname === base + '/members') return reply(config, 200, { items: [{ userId: profile.id, displayName: 'Alex', role: 'Owner' }, { userId: memberId, displayName: 'Teammate', role: 'Member' }] })
    if (path.pathname === base + '/tasks' && config.method === 'post') {
      const values = JSON.parse(config.data); state.posts.push(values); await state.wait
      if (state.createStatus !== 201) return reply(config, state.createStatus, { errors: { assigneeId: ['Choose a current member.'] } })
      const created = { ...item(), ...values }; state.tasks.push(created); return reply(config, 201, created)
    }
    if (path.pathname === base + '/tasks') {
      if (state.listStatus !== 200) return reply(config, state.listStatus)
      const items = state.tasks.filter(task => task.title.toLowerCase().includes((path.searchParams.get('search') ?? '').toLowerCase()))
      const page = Number(path.searchParams.get('page') ?? 1); const pageSize = Number(path.searchParams.get('pageSize') ?? 20)
      return reply(config, 200, { items: items.slice((page - 1) * pageSize, page * pageSize), page, pageSize, totalCount: items.length, totalPages: Math.ceil(items.length / pageSize) })
    }
    if (config.method === 'patch' || config.method === 'delete') {
      const data = config.data ? JSON.parse(config.data) : undefined; state.writes.push({ path: path.pathname, method: config.method, data }); await state.writeWait
      if (state.writeStatus !== 200) return reply(config, state.writeStatus, { errors: state.writeErrors })
      const found = state.tasks.find(task => '/tasks/' + task.id === path.pathname.replace(/\/status$/, ''))
      if (!found) return reply(config, 404)
      if (config.method === 'delete') { state.tasks = state.tasks.filter(task => task !== found); return reply(config, 204) }
      Object.assign(found, data); return reply(config, 200, found)
    }
    const task = state.tasks.find(task => path.pathname === '/tasks/' + task.id)
    return reply(config, state.detailStatus !== 200 ? state.detailStatus : task ? 200 : 404, task)
  })
  render(<StrictMode><MemoryRouter initialEntries={[path]}><App client={client} /><Location /></MemoryRouter></StrictMode>)
  return state
}
describe('Task pages', () => {
  it('does not restore an assignment cleared by removal while an owner edit form was open', async () => {
    const state = setup('/tasks/' + taskId + '/edit', [{ ...item(), assigneeId: memberId }]); const user = userEvent.setup()
    await screen.findByLabelText('Task title'); await waitFor(() => expect(screen.getByLabelText('Assignee')).toHaveValue(memberId))
    // Another request removed the member and cleared the persisted assignment.
    state.tasks = state.tasks.map(task => ({ ...task, assigneeId: null }))
    await user.clear(screen.getByLabelText('Task title')); await user.type(screen.getByLabelText('Task title'), 'Safe stale edit'); await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByRole('heading', { name: 'Safe stale edit' })).toBeVisible(); expect(state.writes[0].data).toEqual({ title: 'Safe stale edit' }); expect(state.tasks[0].assigneeId).toBeNull(); expect(screen.getByText('Unassigned', { exact: true })).toBeVisible()
  })
  it('retains a rejected stale-assignee draft and lets the owner explicitly retry without that assignment', async () => {
    const state = setup('/tasks/' + taskId + '/edit', [item()]); const user = userEvent.setup()
    await user.type(await screen.findByLabelText('Description (optional)'), 'Keep this work'); await waitFor(() => expect(screen.getByLabelText('Assignee')).toBeEnabled()); await user.selectOptions(screen.getByLabelText('Assignee'), memberId)
    state.writeStatus = 400; state.writeErrors = { assigneeId: ['Choose the project owner or a current member.'] }
    await user.click(screen.getByRole('button', { name: 'Save changes' })); expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.getByLabelText('Assignee')).toHaveAccessibleDescription('Choose the project owner or a current member.'); expect(screen.getByLabelText('Description (optional)')).toHaveValue('Keep this work'); expect(state.writes).toHaveLength(1)
    state.writeStatus = 200; await user.selectOptions(screen.getByLabelText('Assignee'), ''); await user.click(screen.getByRole('button', { name: 'Save changes' })); expect(await screen.findByRole('heading', { name: 'First task' })).toBeVisible(); expect(state.writes).toHaveLength(2); expect(state.writes[1].data).toEqual({ description: 'Keep this work' }); expect(state.tasks[0].assigneeId).toBeNull()
  })
  it('prefills editing and submits only changed fields while preserving untouched microsecond deadlines', async () => {
    const state = setup('/tasks/' + taskId, [{ ...item(), description: 'Context', priority: 'High', assigneeId: memberId, dueDateUtc: '2026-11-01T12:00:00.123456Z' }]); const user = userEvent.setup()
    await user.click(await screen.findByRole('link', { name: 'Edit task' })); expect(await screen.findByLabelText('Task title')).toHaveValue('First task'); expect(screen.getByLabelText('Description (optional)')).toHaveValue('Context')
    await user.clear(screen.getByLabelText('Task title')); await user.type(screen.getByLabelText('Task title'), 'Renamed'); await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByRole('heading', { name: 'Renamed' })).toBeVisible(); expect(state.writes[0].data).toEqual({ title: 'Renamed' }); expect(screen.getByText('Task updated.')).toBeVisible()
  })
  it('clears nullable fields explicitly and skips unchanged edit requests', async () => {
    const state = setup('/tasks/' + taskId + '/edit', [{ ...item(), description: 'Context', assigneeId: memberId, dueDateUtc: '2026-11-01T12:00:00Z' }]); const user = userEvent.setup()
    await screen.findByLabelText('Task title'); await user.click(screen.getByRole('button', { name: 'Save changes' })); expect(await screen.findByRole('heading', { name: 'First task' })).toBeVisible(); expect(state.writes).toHaveLength(0)
    await user.click(screen.getByRole('link', { name: 'Edit task' })); await user.clear(await screen.findByLabelText('Description (optional)')); await waitFor(() => expect(screen.getByLabelText('Assignee')).toBeEnabled()); await user.selectOptions(screen.getByLabelText('Assignee'), ''); fireEvent.change(screen.getByLabelText('Due date and time (optional)'), { target: { value: '' } })
    await user.click(screen.getByRole('button', { name: 'Save changes' })); expect(await screen.findByRole('heading', { name: 'First task' })).toBeVisible(); expect(state.writes[0].data).toEqual({ description: null, assigneeId: null, dueDateUtc: null })
  })
  it('updates status once while pending and reloads detail after success', async () => {
    const state = setup('/tasks/' + taskId, [item()]); const user = userEvent.setup(); await screen.findByLabelText('Task status')
    expect(screen.getByRole('button', { name: 'Update status' })).toBeDisabled(); await user.selectOptions(screen.getByLabelText('Task status'), 'Done')
    let release!: () => void; state.writeWait = new Promise<void>(resolve => { release = resolve }); await user.dblClick(screen.getByRole('button', { name: 'Update status' })); expect(state.writes).toHaveLength(1); expect(screen.getByLabelText('Task status')).toBeDisabled()
    await act(async () => { release() }); expect(await screen.findByText('Task status updated.')).toBeVisible(); expect(screen.getByLabelText('Task status')).toHaveValue('Done'); expect(state.writes[0].data).toEqual({ status: 'Done' })
  })
  it('offers edit and status controls to members but hides owner deletion', async () => {
    const state = setup('/tasks/' + taskId, [item()]); state.projectOwnerId = 'other'
    expect(await screen.findByRole('link', { name: 'Edit task' })).toBeVisible(); expect(screen.getByLabelText('Task status')).toBeVisible(); await waitFor(() => expect(state.calls).toContain(base)); expect(screen.queryByRole('button', { name: 'Delete task' })).not.toBeInTheDocument()
  })
  it('confirms deletion, permits Escape and removes the task only after success', async () => {
    const state = setup('/tasks/' + taskId, [item()]); const user = userEvent.setup(); await user.click(await screen.findByRole('button', { name: 'Delete task' })); expect(screen.getByRole('button', { name: 'Keep task' })).toHaveFocus(); await user.keyboard('{Escape}'); expect(state.writes).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Delete task' })); let release!: () => void; state.writeWait = new Promise<void>(resolve => { release = resolve }); await user.dblClick(screen.getByRole('button', { name: 'Confirm task deletion' })); expect(state.writes).toHaveLength(1); expect(state.tasks).toHaveLength(1)
    await act(async () => { release() }); expect(await screen.findByText('Task deleted.')).toBeVisible(); expect(await screen.findByRole('heading', { name: 'No tasks yet.' })).toBeVisible()
  })
  it('keeps edit input and delete confirmation after failures for explicit retry', async () => {
    const state = setup('/tasks/' + taskId + '/edit', [item()]); state.writeStatus = 400; const user = userEvent.setup(); await user.clear(await screen.findByLabelText('Task title')); await user.type(screen.getByLabelText('Task title'), 'Retain draft'); await user.click(screen.getByRole('button', { name: 'Save changes' })); const alert = await screen.findByRole('alert'); await waitFor(() => expect(alert).toHaveFocus()); expect(screen.getByLabelText('Task title')).toHaveValue('Retain draft')
    state.writeStatus = 200; await user.click(screen.getByRole('button', { name: 'Save changes' })); await screen.findByRole('heading', { name: 'Retain draft' }); await user.click(await screen.findByRole('button', { name: 'Delete task' })); state.writeStatus = 500; await user.click(screen.getByRole('button', { name: 'Confirm task deletion' })); expect(await screen.findByRole('alert')).toBeVisible(); expect(state.tasks).toHaveLength(1)
    state.writeStatus = 200; await user.click(screen.getByRole('button', { name: 'Confirm task deletion' })); expect(await screen.findByText('Task deleted.')).toBeVisible()
  })
  it('ignores a pending edit response after an account switch', async () => {
    const state = setup('/tasks/' + taskId + '/edit', [item()]); const user = userEvent.setup(); await user.clear(await screen.findByLabelText('Task title')); await user.type(screen.getByLabelText('Task title'), 'Private edit'); let release!: () => void; state.writeWait = new Promise<void>(resolve => { release = resolve }); await user.click(screen.getByRole('button', { name: 'Save changes' }))
    state.currentProfile = { ...profile, id: 'other', displayName: 'Other' }; state.detailStatus = 404; fireEvent(window, new Event('focus')); expect(await screen.findByRole('heading', { name: 'Task or project not available.' })).toBeVisible(); await act(async () => { release() }); expect(screen.getByTestId('location')).toHaveTextContent('/tasks/' + taskId + '/edit'); expect(screen.queryByDisplayValue('Private edit')).not.toBeInTheDocument()
  })
  it('protects task creation before issuing any project request', async () => {
    const state = setup(base + '/tasks/new', [], false)
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(state.calls).toHaveLength(0)
  })
  it('creates from an empty list with default priority and explicit nullable fields', async () => {
    const state = setup(); const user = userEvent.setup()
    expect(await screen.findByRole('heading', { name: 'No tasks yet.' })).toBeVisible()
    await user.click(screen.getByRole('link', { name: 'New task' })); await user.type(await screen.findByLabelText('Task title'), ' First task ')
    expect(screen.getByLabelText('Priority')).toHaveValue('Medium'); await user.click(screen.getByRole('button', { name: 'Create task' }))
    expect(await screen.findByRole('heading', { name: 'First task' })).toBeVisible(); expect(screen.getByText('Task created.')).toBeVisible()
    expect(screen.getByTestId('location')).toHaveTextContent('/tasks/' + taskId)
    expect(state.posts).toEqual([{ title: 'First task', description: null, priority: 'Medium', assigneeId: null, dueDateUtc: null }])
    await user.click(screen.getByRole('link', { name: 'Back to tasks' })); expect(await screen.findByRole('link', { name: 'First task' })).toBeVisible()
  })
  it('sends an eligible opaque assignee ID and converts a local deadline to UTC while rendering text safely', async () => {
    const state = setup(base + '/tasks/new'); const user = userEvent.setup()
    await user.type(await screen.findByLabelText('Task title'), 'Assigned task'); await user.type(screen.getByLabelText('Description (optional)'), '<img src=x onerror=alert(1)>')
    await user.selectOptions(screen.getByLabelText('Priority'), 'High'); await waitFor(() => expect(screen.getByLabelText('Assignee')).toBeEnabled())
    await user.selectOptions(screen.getByLabelText('Assignee'), memberId); fireEvent.change(screen.getByLabelText('Due date and time (optional)'), { target: { value: '2026-11-01T12:30:00' } })
    await user.click(screen.getByRole('button', { name: 'Create task' })); expect(await screen.findByRole('heading', { name: 'Assigned task' })).toBeVisible()
    expect(state.posts[0]).toMatchObject({ priority: 'High', assigneeId: memberId, dueDateUtc: new Date('2026-11-01T12:30:00').toISOString() })
    expect(screen.getByText('<img src=x onerror=alert(1)>')).toBeVisible(); expect(document.querySelector('.project-description img')).toBeNull()
  })
  it('focuses invalid title without posting and prevents duplicate pending creates', async () => {
    const state = setup(base + '/tasks/new'); const user = userEvent.setup(); await screen.findByLabelText('Task title')
    await user.click(screen.getByRole('button', { name: 'Create task' })); expect(screen.getByLabelText('Task title')).toHaveFocus(); expect(state.posts).toHaveLength(0)
    await user.type(screen.getByLabelText('Task title'), 'Pending'); expect(screen.getByLabelText('Task title')).toHaveAttribute('aria-invalid', 'false'); let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve })
    await user.dblClick(screen.getByRole('button', { name: 'Create task' })); expect(state.posts).toHaveLength(1); expect(screen.getByLabelText('Task title')).toBeDisabled(); expect(screen.getByRole('button', { name: 'Cancel' })).toBeDisabled()
    await act(async () => { release() }); expect(await screen.findByRole('heading', { name: 'Pending' })).toBeVisible()
  })
  it('retains input and associates server assignee errors after removal', async () => {
    const state = setup(base + '/tasks/new'); state.createStatus = 400; const user = userEvent.setup()
    await user.type(await screen.findByLabelText('Task title'), 'Keep draft'); await waitFor(() => expect(screen.getByLabelText('Assignee')).toBeEnabled()); await user.selectOptions(screen.getByLabelText('Assignee'), memberId)
    await user.click(screen.getByRole('button', { name: 'Create task' })); const alert = await screen.findByRole('alert'); await waitFor(() => expect(alert).toHaveFocus())
    expect(screen.getByLabelText('Task title')).toHaveValue('Keep draft'); expect(screen.getByLabelText('Assignee')).toHaveAccessibleDescription('Choose a current member.')
    expect(screen.getByRole('button', { name: 'Create task' })).toBeEnabled()
  })
  it('paginates and searches with URL history', async () => {
    setup(base + '/tasks', Array.from({ length: 23 }, (_, i) => item('Task ' + i, String(i + 1)))); const user = userEvent.setup()
    await screen.findByText('23 tasks'); await user.selectOptions(screen.getByLabelText('Per page'), '10'); await user.click(screen.getByRole('button', { name: 'Apply' })); expect(await screen.findByText('Page 1 of 3')).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Next' })); expect(await screen.findByText('Page 2 of 3')).toBeVisible(); await user.click(screen.getByRole('button', { name: 'Browser back' })); expect(await screen.findByText('Page 1 of 3')).toBeVisible()
    await user.type(screen.getByLabelText('Search tasks'), 'Task 22'); await user.click(screen.getByRole('button', { name: 'Apply' })); expect(await screen.findByText('1 task')).toBeVisible(); expect(screen.getByTestId('location')).toHaveTextContent('search=Task+22')
    await user.click(screen.getByRole('button', { name: 'Reset' })); expect(await screen.findByText('23 tasks')).toBeVisible()
  })
  it('recovers list failures and hides missing task data without ending the session', async () => {
    const state = setup(); state.listStatus = 503; const user = userEvent.setup(); expect(await screen.findByRole('alert')).toBeVisible()
    state.listStatus = 200; state.tasks.push(item()); await user.click(screen.getByRole('button', { name: 'Try again' })); await user.click(await screen.findByRole('link', { name: 'First task' }))
    expect(await screen.findByRole('heading', { name: 'First task' })).toBeVisible(); state.tasks = []; await user.click(screen.getByRole('button', { name: 'Browser back' })); await screen.findByText('No tasks yet.')
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeVisible()
  })
  it('uses a generic unavailable view for a private direct task', async () => {
    setup('/tasks/' + taskId)
    expect(await screen.findByRole('heading', { name: 'Task or project not available.' })).toBeVisible(); expect(screen.getByRole('button', { name: 'Sign out' })).toBeVisible(); expect(document.title).not.toContain('First task')
  })
  it('redirects an expired direct task session to sign-in', async () => {
    const state = setup('/tasks/' + taskId, [item()]); state.detailStatus = 401
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible()
    expect(screen.queryByRole('heading', { name: 'First task' })).not.toBeInTheDocument()
  })
  it('discards a private task draft and ignores a pending response after an account switch', async () => {
    const state = setup(base + '/tasks/new'); const user = userEvent.setup(); await user.type(await screen.findByLabelText('Task title'), 'Private draft')
    let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve }); await user.click(screen.getByRole('button', { name: 'Create task' }))
    state.currentProfile = { ...profile, id: 'other', displayName: 'Other' }; state.projectStatus = 404
    fireEvent(window, new Event('focus')); expect(await screen.findByRole('heading', { name: 'Task or project not available.' })).toBeVisible()
    await act(async () => { release() }); expect(screen.getByTestId('location')).toHaveTextContent(base + '/tasks/new'); expect(screen.queryByDisplayValue('Private draft')).not.toBeInTheDocument()
  })
})
