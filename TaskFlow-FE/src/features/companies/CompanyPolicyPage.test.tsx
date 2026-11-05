import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { InternalAxiosRequestConfig } from 'axios'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'

function setup(role = 'Owner') {
  const auth = server(true), calls: InternalAxiosRequestConfig[] = []
  const policy = { version: '9007199254741111', auditRetentionDays: 365, deletedContentRetentionDays: 30, legalHold: true, auditExportsEnabled: true, automaticPurgeEnabled: false,
    capacity: { reservedMemberSeats: 0, activeProjects: 20, activeTasks: 50, activeMembers: 2, retainedProjects: 21, retainedTasks: 55, projectLimit: 20, taskLimit: 2000, memberLimit: 25, tasksPerProjectLimit: 500, retainedProjectLimit: 100, retainedTaskLimit: 10000 } }
  const client = createApiClient(async c => {
    calls.push(c)
    if (c.baseURL === '/api/v1') return auth.adapter(c)
    if (c.baseURL === '/api/v2') return reply(c, 200, { id: '1', name: 'Acme', role })
    if (c.url === '/policy' && c.method === 'put') { const body = JSON.parse(c.data); policy.auditRetentionDays = body.auditRetentionDays; policy.auditExportsEnabled = body.auditExportsEnabled; policy.version = '9007199254741112'; return reply(c, 204) }
    return reply(c, 200, policy)
  })
  render(<MemoryRouter initialEntries={['/companies/1/policy']}><App client={client} companiesEnabled lifecycleEnabled /></MemoryRouter>)
  return calls
}
describe('company policy', () => {
  it('shows aggregate capacity and requires confirmation/password for an audited policy update', async () => {
    const calls = setup(), user = userEvent.setup()
    expect(await screen.findByRole('heading', { name: 'Policy & capacity' })).toBeInTheDocument()
    await screen.findByText('20 / 20')
    expect(screen.getByText(/Automatic deletion is off/)).toBeInTheDocument()
    await user.selectOptions(screen.getByLabelText('Audit history review period'), '180')
    await user.click(screen.getByLabelText('Allow company audit downloads'))
    await user.type(screen.getByLabelText('Current password'), 'Synthetic proof')
    await user.click(screen.getByRole('button', { name: 'Review change' }))
    expect(calls.filter(c => c.method === 'put')).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Confirm change' }))
    await waitFor(() => expect(calls.filter(c => c.method === 'put')).toHaveLength(1))
    const request = calls.find(c => c.method === 'put')!
    expect(request.baseURL).toBe('/api/v2/workspaces/1')
    expect(request.headers.get('X-CSRF-TOKEN')).toBeTruthy()
    expect(JSON.parse(request.data)).toEqual({ expectedVersion: '9007199254741111', auditRetentionDays: 180, deletedContentRetentionDays: 30, legalHold: true, auditExportsEnabled: false, password: 'Synthetic proof' })
    await waitFor(() => expect(screen.getByLabelText('Current password')).toHaveValue(''))
  })
  it('lets admins review capacity and policy without showing owner controls', async () => {
    setup('Admin')
    await screen.findByText('20 / 20')
    expect(screen.getByText('Only an active company Owner can change this policy.')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Review change' })).not.toBeInTheDocument()
    expect(screen.queryByLabelText('Current password')).not.toBeInTheDocument()
  })
})
