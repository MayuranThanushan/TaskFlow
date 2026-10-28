import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import type { InternalAxiosRequestConfig } from 'axios'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'

function Location() { return <span data-testid="location">{useLocation().pathname}</span> }
function setup(path = '/companies/1/team', role: 'Owner' | 'Admin' | 'Member' | 'Guest' = 'Owner', projectRole = 'Owner') {
  const auth = server(true)
  const state = { writes: [] as InternalAxiosRequestConfig[], status: 204, wait: null as Promise<void> | null }
  const members = [{ userId: profile.id, displayName: 'Alex', role, state: 'Active' }, { userId: '2', displayName: 'Teammate', role: 'Member', state: 'Active' }]
  const client = createApiClient(async c => {
    if (c.baseURL === '/api/v1') return auth.adapter(c)
    if (c.baseURL === '/api/v2' && c.url?.startsWith('/workspaces?')) return reply(c, 200, { items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
    if (c.baseURL === '/api/v2') return reply(c, 200, { id: '1', name: 'Acme', role })
    if (c.method !== 'get') { state.writes.push(c); await state.wait; return reply(c, state.status) }
    if (c.url?.startsWith('/members')) return reply(c, 200, { items: role === 'Member' ? members.map(m => ({ ...m, role: null, state: null })) : members, page: 1, pageSize: 20, totalCount: 2, totalPages: 1 })
    if (c.url?.startsWith('/audit')) return reply(c, 200, { items: [{ id: '9', actorUserId: '1', targetUserId: '2', projectId: '10', action: 'ProjectRoleChanged', fromRole: 'Editor', toRole: 'Viewer', createdAtUtc: '2026-10-28T10:00:00Z' }], page: 1, pageSize: 20, totalCount: 1, totalPages: 1 })
    if (c.url === '/projects/10/members') return reply(c, 200, { items: [{ userId: profile.id, displayName: profile.displayName, role: projectRole, joinedAtUtc: '2026-10-28T10:00:00Z' }, { userId: '2', displayName: 'Teammate', role: 'Editor', joinedAtUtc: '2026-10-28T10:00:00Z' }] })
    if (c.url === '/projects/10') return reply(c, 200, { id: '10', name: 'Private project', ownerId: projectRole === 'Owner' ? profile.id : '2', description: null, createdAtUtc: '2026-10-28T10:00:00Z', updatedAtUtc: '2026-10-28T10:00:00Z', totalTasks: 0, completedTasks: 0, progressPercentage: 0 })
    if (c.url?.startsWith('/projects/10/tasks')) return reply(c, 200, { items: [], totalCount: 0, totalPages: 0, page: 1, pageSize: 20 })
    return auth.adapter(c)
  })
  render(<StrictMode><MemoryRouter initialEntries={[path]}><App client={client} companiesEnabled lifecycleEnabled /><Location /></MemoryRouter></StrictMode>)
  return state
}
describe('company lifecycle screens', () => {
  it('confirms suspension once, prevents duplicate pending writes, and retains retry after rejection', async () => {
    const state = setup(), user = userEvent.setup()
    const form = await screen.findByRole('form', { name: 'Change access for Teammate' })
    await user.click(within(form).getByRole('button', { name: 'Review change' }))
    expect(state.writes).toHaveLength(0)
    let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve }); state.status = 409
    await user.click(screen.getByRole('button', { name: 'Confirm change' }))
    expect(screen.getByRole('button', { name: 'Saving change...' })).toBeDisabled()
    await act(async () => { release() })
    expect(await screen.findByRole('alert')).toHaveTextContent('Transfer active owned projects')
    expect(state.writes).toHaveLength(1)
    expect(state.writes[0].baseURL).toBe('/api/v2/workspaces/1')
    expect(JSON.parse(state.writes[0].data)).toEqual({ state: 'Suspended' })
  })
  it('requires password for Owner promotion and clears the password after a denied request', async () => {
    const state = setup(), user = userEvent.setup(); state.status = 403
    const form = await screen.findByRole('form', { name: 'Change role for Teammate' })
    await user.selectOptions(within(form).getByLabelText('Company role'), 'Owner')
    await user.type(within(form).getByLabelText('Current password'), 'SyntheticPassword123!')
    await user.click(within(form).getByRole('button', { name: 'Review change' }))
    await user.click(screen.getByRole('button', { name: 'Confirm change' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('password verification failed')
    expect(within(form).getByLabelText('Current password')).toHaveValue('')
    expect(JSON.parse(state.writes[0].data)).toEqual({ role: 'Owner', password: 'SyntheticPassword123!' })
  })
  it('keeps private-project controls separate from company administration and hides elevated team controls', async () => {
    setup('/companies/1/team', 'Member')
    expect(await screen.findByText('Teammate', { exact: true })).toBeInTheDocument()
    expect(screen.queryByRole('form', { name: 'Change role for Teammate' })).not.toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Company audit' })).not.toBeInTheDocument()
  })
  it('lets a Guest leave with confirmation while hiding the company directory', async () => {
    const state = setup('/companies/1/team', 'Guest'), user = userEvent.setup()
    expect(await screen.findByText(/As a Guest/)).toBeInTheDocument()
    expect(screen.queryByText('Teammate', { exact: true })).not.toBeInTheDocument()
    await user.click(within(screen.getByRole('form', { name: 'Leave company' })).getByRole('button', { name: 'Review change' }))
    await user.click(screen.getByRole('button', { name: 'Confirm change' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/companies'))
    expect(state.writes[0].url).toBe('/leave')
  })
  it('shows governance IDs without treating a company audit as private project content', async () => {
    setup('/companies/1/audit')
    expect(await screen.findByText('ProjectRoleChanged')).toBeInTheDocument()
    expect(screen.getByRole('columnheader', { name: 'Project ID' })).toBeInTheDocument()
    expect(screen.queryByText('Private project')).not.toBeInTheDocument()
  })
  it('hides task creation from project Viewers', async () => {
    setup('/companies/1/projects/10/tasks', 'Member', 'Viewer')
    expect(await screen.findByText('0 tasks', { exact: true })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'New task' })).not.toBeInTheDocument()
  })
  it('redirects legacy resource URLs to company selection in lifecycle mode', async () => {
    setup('/projects/10')
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/companies'))
    expect(screen.queryByRole('heading', { name: 'Private project' })).not.toBeInTheDocument()
  })
})
