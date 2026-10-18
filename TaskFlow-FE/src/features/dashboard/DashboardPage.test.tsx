import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'
import type { DashboardTotals } from './DashboardPage'
const populated: DashboardTotals = { totalProjects: 2, totalTasks: 7, tasksByStatus: { toDo: 3, inProgress: 2, done: 2 }, assignedToMe: 4, overdueTasks: 1 }
const empty: DashboardTotals = { totalProjects: 0, totalTasks: 0, tasksByStatus: { toDo: 0, inProgress: 0, done: 0 }, assignedToMe: 0, overdueTasks: 0 }
const value = (label: string) => screen.getByText(label, { selector: 'dt' }).nextElementSibling
function setup(authenticated = true, totals = populated) {
  const auth = server(authenticated)
  const state = { totals, currentProfile: { ...profile }, status: 200, wait: null as Promise<void> | null, calls: 0 }
  const client = createApiClient(async config => {
    if (config.url === '/auth/me') return reply(config, auth.state.authenticated ? 200 : 401, state.currentProfile)
    if (config.url === '/dashboard') { state.calls++; const snapshot = structuredClone(state.totals); const status = state.status; await state.wait; return reply(config, status, snapshot) }
    if (config.url?.startsWith('/projects')) return reply(config, 200, { items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
    return auth.adapter(config)
  })
  render(<StrictMode><MemoryRouter initialEntries={['/dashboard']}><App client={client} /></MemoryRouter></StrictMode>)
  return state
}
describe('Dashboard overview', () => {
  it('protects the overview before requesting any totals', async () => {
    const state = setup(false); expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(state.calls).toBe(0)
  })
  it('shows live scoped totals and all status counts with the account ID', async () => {
    setup(); await screen.findByRole('heading', { name: 'Workspace totals' })
    expect(value('Projects')).toHaveTextContent('2'); expect(value('Tasks')).toHaveTextContent('7'); expect(value('Assigned to me')).toHaveTextContent('4'); expect(value('Overdue tasks')).toHaveTextContent('1'); expect(value('To Do')).toHaveTextContent('3'); expect(value('In Progress')).toHaveTextContent('2'); expect(value('Completed')).toHaveTextContent('2'); expect(screen.getByText('Your account ID: ' + profile.id)).toBeVisible(); expect(screen.queryByText('Room for your next idea.')).not.toBeInTheDocument()
  })
  it('does not show fabricated zero counts while loading and supports an empty workspace', async () => {
    const state = setup(true, empty); let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve })
    expect(await screen.findByText('Loading your overview...')).toBeVisible(); expect(screen.queryByText('Projects', { selector: 'dt' })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Refresh overview' })).toBeDisabled()
    await act(async () => { release() }); expect(await screen.findByRole('heading', { name: 'Room for your next idea.' })).toBeVisible(); expect(value('Tasks')).toHaveTextContent('0'); expect(screen.getByRole('link', { name: 'Create a project' })).toBeVisible()
  })
  it('recovers a safe error and refreshes totals after changes and navigation', async () => {
    const state = setup(); state.status = 503; const user = userEvent.setup(); expect(await screen.findByRole('alert')).toBeVisible(); expect(screen.queryByText('Tasks', { selector: 'dt' })).not.toBeInTheDocument()
    state.status = 200; await user.click(screen.getByRole('button', { name: 'Try again' })); await screen.findByRole('heading', { name: 'Workspace totals' }); expect(value('Tasks')).toHaveTextContent('7')
    state.totals = { ...empty, totalProjects: 1 }; await user.click(screen.getByRole('button', { name: 'Refresh overview' })); expect(await screen.findByText('Your projects are ready. Add a task to start tracking progress.')).toBeVisible(); expect(value('Tasks')).toHaveTextContent('0')
    await user.click(screen.getByRole('link', { name: 'Browse projects' })); await screen.findByRole('heading', { name: 'Your first project starts here.' }); state.totals = populated; await user.click(screen.getByRole('link', { name: 'Overview' })); await screen.findByRole('heading', { name: 'Workspace totals' }); expect(value('Tasks')).toHaveTextContent('7')
  })
  it('expires a session on a dashboard 401 without showing counts', async () => {
    const state = setup(); state.status = 401; expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(screen.queryByText('Tasks', { selector: 'dt' })).not.toBeInTheDocument()
  })
  it('clears the old totals on account change and ignores a late previous-account response', async () => {
    const state = setup(); let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve }); await waitFor(() => expect(state.calls).toBeGreaterThan(0))
    state.currentProfile = { ...profile, id: 'other', displayName: 'Other' }; state.totals = empty; state.wait = null; fireEvent(window, new Event('focus'))
    expect(await screen.findByRole('heading', { name: 'Welcome back, Other.' })).toBeVisible(); expect(await screen.findByRole('heading', { name: 'Room for your next idea.' })).toBeVisible(); expect(value('Tasks')).toHaveTextContent('0')
    await act(async () => { release() }); expect(value('Tasks')).toHaveTextContent('0'); expect(screen.queryByText('Welcome back, Alex.')).not.toBeInTheDocument()
  })
})
