import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'

const firstId = '9007199254741201'
const rows = () => [{ id: firstId, workspaceId: '1', kind: 'TaskAssigned', projectId: '8', projectName: 'Private project', taskId: '9007199254741999', taskTitle: '<script>Review work</script>', createdAtUtc: '2026-11-12T09:00:00Z', readAtUtc: null as string | null }, { id: '9007199254741200', workspaceId: '1', kind: 'TaskAssigned', projectId: '8', projectName: 'Private project', taskId: '9007199254741998', taskTitle: 'Previously reviewed', createdAtUtc: '2026-11-12T08:00:00Z', readAtUtc: '2026-11-12T08:30:00Z' }]
function Tracker() { const location = useLocation(), navigate = useNavigate(); return <><span data-testid="location">{location.pathname + location.search}</span><button onClick={() => void navigate('/companies/2/notifications')}>Switch to Beta</button></> }
function setup(path = '/companies/1/notifications', options: { authenticated?: boolean; enabled?: boolean } = {}) {
  const auth = server(options.authenticated ?? true)
  const state = { items: rows(), listStatus: 200, writeStatus: 200, code: '', lists: [] as string[], writes: [] as { url: string; base: string; data: Record<string, unknown> }[], wait: null as Promise<void> | null, writeWait: null as Promise<void> | null, preferences: { inAppEnabled: true, taskAssignedEnabled: true, emailAvailable: false, version: '0' } }
  const client = createApiClient(async c => {
    if (c.baseURL === '/api/v1') return auth.adapter(c)
    if (c.baseURL === '/api/v2') return reply(c, 200, { id: c.url?.endsWith('/2') ? '2' : '1', name: c.url?.endsWith('/2') ? 'Beta' : 'Alpha', role: 'Member' })
    const beta = c.baseURL?.endsWith('/2'), items = beta ? [] : state.items
    if (c.method === 'get' && c.url === '/notifications/preferences') return reply(c, 200, beta ? { ...state.preferences, version: '0', inAppEnabled: true } : state.preferences)
    if (c.method === 'get' && c.url === '/notifications/unread-count') return reply(c, 200, { unreadCount: items.filter(n => !n.readAtUtc).length })
    if (c.method === 'get' && c.url?.startsWith('/notifications?')) {
      state.lists.push(c.url); const snapshot = items.map(n => ({ ...n })); await state.wait
      const params = new URLSearchParams(c.url.split('?')[1]), page = Number(params.get('page')), unreadOnly = params.get('unreadOnly') === 'true', filtered = unreadOnly ? snapshot.filter(n => !n.readAtUtc) : snapshot
      return reply(c, state.listStatus, { items: filtered.slice((page - 1) * 20, page * 20), totalCount: filtered.length, totalPages: Math.ceil(filtered.length / 20), page, pageSize: 20, unreadCount: snapshot.filter(n => !n.readAtUtc).length })
    }
    if (c.method !== 'get' && c.url?.startsWith('/notifications')) {
      const data = JSON.parse(c.data as string) as Record<string, unknown>; state.writes.push({ url: c.url, base: c.baseURL!, data }); await state.writeWait
      if (state.writeStatus !== 200) return reply(c, state.writeStatus, { code: state.code })
      if (c.url === '/notifications/preferences') { state.preferences = { inAppEnabled: data.inAppEnabled as boolean, taskAssignedEnabled: data.taskAssignedEnabled as boolean, emailAvailable: false, version: String(Number(state.preferences.version) + 1) }; return reply(c, 200, state.preferences) }
      if (c.url === '/notifications/read') state.items = state.items.map(n => (data.notificationIds as string[]).includes(n.id) ? { ...n, readAtUtc: '2026-11-12T10:00:00Z' } : n)
      else state.items = state.items.map(n => c.url?.endsWith('/' + n.id) ? { ...n, readAtUtc: data.isRead ? '2026-11-12T10:00:00Z' : null } : n)
      return reply(c, 204)
    }
    return reply(c, 404)
  })
  render(<MemoryRouter initialEntries={[path]}><App client={client} companiesEnabled lifecycleEnabled notificationsEnabled={options.enabled ?? true} /><Tracker /></MemoryRouter>)
  return { state, auth, user: userEvent.setup() }
}

describe('recipient inbox', () => {
  it('protects anonymous navigation and does not expose routes or requests when disabled', async () => {
    const { state } = setup('/companies/1/notifications', { authenticated: false })
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(state.lists).toHaveLength(0)
  })
  it('keeps the inbox disabled until the frontend rollout flag is enabled', async () => {
    const { state } = setup('/companies/1/notifications', { enabled: false })
    await screen.findByRole('heading', { name: 'That company page is not here.' }); expect(state.lists).toHaveLength(0); expect(screen.queryByRole('link', { name: /^Inbox/ })).not.toBeInTheDocument()
  })
  it('renders escaped current titles, opaque scoped links and reversible read state', async () => {
    const { state, user } = setup(); const link = await screen.findByRole('link', { name: '<script>Review work</script>' })
    expect(link).toHaveAttribute('href', '/companies/1/tasks/9007199254741999'); expect(document.querySelector('.notification-list script')).toBeNull()
    expect(await screen.findByRole('link', { name: 'Inbox, 1 unread' })).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Mark <script>Review work</script> as read' }))
    await screen.findByRole('button', { name: 'Mark <script>Review work</script> as unread' }); expect(state.writes[0]).toMatchObject({ base: '/api/v2/workspaces/1', url: '/notifications/' + firstId, data: { isRead: true } })
    await user.click(screen.getByRole('button', { name: 'Mark <script>Review work</script> as unread' })); await screen.findByRole('button', { name: 'Mark <script>Review work</script> as read' })
    expect(state.writes[1].data).toEqual({ isRead: false })
  })
  it('marks only unread IDs on the visible page without duplicate submissions', async () => {
    const { state, user } = setup(); await screen.findByRole('link', { name: '<script>Review work</script>' })
    let release!: () => void; state.writeWait = new Promise<void>(resolve => { release = resolve })
    await user.dblClick(screen.getByRole('button', { name: 'Mark page as read' })); expect(state.writes).toHaveLength(1); expect(state.writes[0].data).toEqual({ notificationIds: [firstId] })
    expect(screen.getByRole('button', { name: 'Mark page as read' })).toBeDisabled(); await act(async () => { release() })
    await screen.findByText('0 unread notifications'); expect(state.writes).toHaveLength(1)
  })
  it('keeps filters in the URL, paginates and recovers invalid or empty bookmarks', async () => {
    const { state, user } = setup('/companies/1/notifications?view=unread&page=1'); state.items = Array.from({ length: 21 }, (_, n) => ({ ...rows()[0], id: String(100 + n), taskTitle: 'Notification ' + n }))
    await screen.findByText('21 unread notifications'); await user.click(screen.getByRole('button', { name: 'Next' })); await screen.findByRole('link', { name: 'Notification 20' }); expect(screen.getByTestId('location')).toHaveTextContent('view=unread&page=2')
    await user.click(screen.getByRole('button', { name: 'All notifications' })); await screen.findByRole('link', { name: 'Notification 0' }); expect(screen.getByTestId('location')).toHaveTextContent('view=all&page=1')
  })
  it('rejects invalid filters without fetching a list', async () => {
    const { state, user } = setup('/companies/1/notifications?page=invalid'); expect(await screen.findByText('This inbox filter is not valid.')).toBeVisible(); expect(state.lists).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Reset inbox filters' })); await screen.findByRole('link', { name: '<script>Review work</script>' })
  })
  it('offers retry after a safe list failure and never invents unread totals', async () => {
    const { state, user } = setup(); state.listStatus = 500
    await screen.findByRole('alert'); expect(screen.queryByText('0 unread notifications')).not.toBeInTheDocument(); state.listStatus = 200
    await user.click(screen.getByRole('button', { name: 'Try again' })); await screen.findByRole('link', { name: '<script>Review work</script>' })
  })
  it('hides all private rows immediately after a revoked write and supports a fresh retry', async () => {
    const { state, user } = setup(); await screen.findByRole('link', { name: '<script>Review work</script>' }); state.writeStatus = 404
    await user.click(screen.getByRole('button', { name: 'Mark page as read' })); await screen.findByRole('heading', { name: 'Inbox unavailable' }); expect(screen.queryByText('Private project')).not.toBeInTheDocument()
    state.writeStatus = 200; state.items = []; await user.click(screen.getByRole('button', { name: 'Reload inbox' })); await screen.findByRole('heading', { name: 'Your inbox is clear' })
  })
  it('ignores a delayed old-company response and clears private rows and preference drafts', async () => {
    const { state, user } = setup(); let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve })
    await waitFor(() => expect(state.lists.length).toBeGreaterThan(0)); await user.click(screen.getByRole('button', { name: 'Switch to Beta' })); await screen.findByText('Beta')
    state.wait = null; await act(async () => { release() }); await screen.findByRole('heading', { name: 'Your inbox is clear' }); expect(screen.queryByText('<script>Review work</script>')).not.toBeInTheDocument()
  })
  it('clears the inbox when the current session expires during a write', async () => {
    const { state, user } = setup(); await screen.findByRole('link', { name: '<script>Review work</script>' }); state.writeStatus = 401
    await user.click(screen.getByRole('button', { name: 'Mark page as read' })); await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })
    expect(screen.queryByText('Private project')).not.toBeInTheDocument()
  })
  it('keeps an already sent write in its original company and ignores its late completion after switching', async () => {
    const { state, user } = setup(); await screen.findByRole('link', { name: '<script>Review work</script>' }); let release!: () => void
    state.writeWait = new Promise<void>(resolve => { release = resolve }); await user.click(screen.getByRole('button', { name: 'Mark page as read' })); await waitFor(() => expect(state.writes).toHaveLength(1))
    await user.click(screen.getByRole('button', { name: 'Switch to Beta' })); await screen.findByRole('heading', { name: 'Your inbox is clear' }); await act(async () => { release() })
    expect(state.writes[0].base).toBe('/api/v2/workspaces/1'); expect(state.writes).toHaveLength(1); expect(screen.queryByText('Inbox updated.')).not.toBeInTheDocument()
  })
})

describe('recipient notification preferences', () => {
  it('saves channel/event preferences for this company only and leaves email unavailable', async () => {
    const { state, user } = setup(); await screen.findByRole('link', { name: '<script>Review work</script>' }); await user.click(screen.getByText('Notification preferences'))
    const form = screen.getByRole('form', { name: 'Notification preferences' }); expect(within(form).getByLabelText('Email notifications')).toBeDisabled()
    await user.click(within(form).getByLabelText('In-app notifications')); expect(within(form).getByLabelText('Tasks assigned to me')).toBeDisabled()
    await user.click(within(form).getByRole('button', { name: 'Save preferences' })); await screen.findByText('Notification preferences saved.')
    expect(state.writes[0]).toMatchObject({ base: '/api/v2/workspaces/1', url: '/notifications/preferences', data: { inAppEnabled: false, taskAssignedEnabled: true, expectedVersion: '0' } })
    expect(screen.getByLabelText('In-app notifications')).not.toBeChecked(); expect(screen.getByRole('link', { name: '<script>Review work</script>' })).toBeVisible()
  })
  it('saves explicit reminder and local digest settings without enabling email', async () => {
    const { state, user } = setup(); await screen.findByRole('link', { name: '<script>Review work</script>' }); await user.click(screen.getByText('Notification preferences'))
    await user.click(screen.getByLabelText('Remind me when assigned tasks are due'))
    await user.click(screen.getByLabelText('Daily digest of overdue tasks and tasks due in the next 24 hours'))
    await user.clear(screen.getByLabelText('Timezone')); await user.type(screen.getByLabelText('Timezone'), 'Asia/Colombo')
    await user.selectOptions(screen.getByLabelText('Daily digest hour'), '17')
    await user.click(screen.getByRole('button', { name: 'Save preferences' })); await screen.findByText('Notification preferences saved.')
    expect(state.writes[0].data).toMatchObject({ dueReminderEnabled: true, dailyDigestEnabled: true, timeZoneId: 'Asia/Colombo', digestHour: 17, expectedVersion: '0' })
    expect(screen.getByLabelText('Email notifications')).toBeDisabled()
  })
  it('retains a conflicting draft and reloads current preferences only on explicit action', async () => {
    const { state, user } = setup(); await screen.findByRole('link', { name: '<script>Review work</script>' }); await user.click(screen.getByText('Notification preferences'))
    await user.click(screen.getByLabelText('Tasks assigned to me')); state.writeStatus = 409; state.code = 'resource.conflict'
    await user.click(screen.getByRole('button', { name: 'Save preferences' })); expect(await screen.findByRole('alert')).toHaveTextContent('Your draft is kept'); expect(screen.getByLabelText('Tasks assigned to me')).not.toBeChecked(); expect(state.writes).toHaveLength(1)
    await user.click(screen.getByRole('button', { name: 'Reload current preferences' })); await waitFor(() => expect(screen.getByLabelText('Tasks assigned to me')).toBeChecked())
  })
})
