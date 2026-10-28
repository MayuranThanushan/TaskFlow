import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import type { InternalAxiosRequestConfig } from 'axios'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'

function Location() {
  const location = useLocation(), navigate = useNavigate()
  return <><span data-testid="location">{location.pathname}</span><button onClick={() => void navigate('/companies/2/projects/new')}>Switch directly to Beta</button></>
}
function setup(path: string, enabled = true) {
  const auth = server(true)
  const state = { calls: [] as InternalAxiosRequestConfig[], listWait: null as Promise<void> | null, csrfWait: null as Promise<void> | null, unavailable: false }
  const companies = [{ id: '1', name: 'Alpha', role: 'Owner' }, { id: '2', name: 'Beta', role: 'Guest' }]
  const client = createApiClient(async c => {
    state.calls.push(c)
    if (c.url === '/auth/csrf') { await state.csrfWait; return auth.adapter(c) }
    if (c.baseURL === '/api/v1') return auth.adapter(c)
    if (c.baseURL === '/api/v2') {
      if (state.unavailable) return reply(c, 404)
      if (c.method === 'post') return reply(c, 201, { id: '3', name: JSON.parse(c.data).name, role: 'Owner' })
      if (c.url?.startsWith('/workspaces?')) return reply(c, 200, { items: companies, totalCount: 2, page: 1, totalPages: 1 })
      const id = c.url?.split('/').at(-1)
      return reply(c, 200, companies.find(x => x.id === id) ?? { id: '3', name: 'Gamma', role: 'Owner' })
    }
    if (c.url?.startsWith('/projects')) {
      await state.listWait
      const item = { id: '10', ownerId: 'user-1', name: c.baseURL?.includes('/1') ? 'Alpha project' : 'Beta project', description: null, totalTasks: 0, completedTasks: 0, progressPercentage: 0, createdAtUtc: '2026-10-28T00:00:00Z', updatedAtUtc: '2026-10-28T00:00:00Z' }
      if (c.method === 'post') return reply(c, 201, item)
      return reply(c, 200, { items: [item], page: 1, totalPages: 1, totalCount: 1 })
    }
    return auth.adapter(c)
  })
  render(<StrictMode><MemoryRouter initialEntries={[path]}><App client={client} companiesEnabled={enabled} /><Location /></MemoryRouter></StrictMode>)
  return state
}

describe('company navigation', () => {
  it('lists companies and creates one using only its name, with a scoped overview URL', async () => {
    const state = setup('/companies'), user = userEvent.setup()
    expect(await screen.findByRole('link', { name: 'Open Alpha' })).toHaveAttribute('href', '/companies/1/dashboard')
    await user.type(screen.getByLabelText('Company name'), 'Gamma')
    await user.click(screen.getByRole('button', { name: 'Create company' }))
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent('/companies/3/dashboard'))
    const create = state.calls.find(c => c.baseURL === '/api/v2' && c.method === 'post')!
    expect(JSON.parse(create.data)).toEqual({ name: 'Gamma' })
    expect(create.headers.get('X-CSRF-TOKEN')).toBeTruthy()
  })
  it('keeps resource links and navigation inside the explicit company URL', async () => {
    const state = setup('/companies/1/projects')
    expect(await screen.findByRole('link', { name: 'Alpha project' })).toHaveAttribute('href', '/companies/1/projects/10')
    expect(screen.getByRole('link', { name: 'Projects' })).toHaveAttribute('href', '/companies/1/projects')
    expect(screen.getByRole('link', { name: 'Tasks' })).toHaveAttribute('href', '/companies/1/projects/10/tasks')
    expect(state.calls.filter(c => c.url?.startsWith('/projects')).every(c => c.baseURL === '/api/v2/workspaces/1')).toBe(true)
  })
  it('clears drafts and cancels a save waiting for CSRF instead of retargeting it', async () => {
    const state = setup('/companies/1/projects/new'), user = userEvent.setup()
    let release!: () => void
    state.csrfWait = new Promise<void>(resolve => { release = resolve })
    await user.type(await screen.findByLabelText('Project name'), 'Private Alpha draft')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await waitFor(() => expect(state.calls.some(c => c.url === '/auth/csrf')).toBe(true))
    await user.click(screen.getByRole('button', { name: 'Switch directly to Beta' }))
    expect(await screen.findByLabelText('Project name')).toHaveValue('')
    await act(async () => { release() })
    expect(state.calls.filter(c => c.url === '/projects' && c.method === 'post')).toHaveLength(0)
    expect(screen.getByTestId('location')).toHaveTextContent('/companies/2/projects/new')
  })
  it('ignores an old company response after switching', async () => {
    const state = setup('/companies/1/projects'), user = userEvent.setup()
    let release!: () => void
    state.listWait = new Promise<void>(resolve => { release = resolve })
    await waitFor(() => expect(state.calls.some(c => c.url === '/projects')).toBe(true))
    await user.click(screen.getByRole('button', { name: 'Switch directly to Beta' }))
    expect(await screen.findByLabelText('Project name')).toHaveValue('')
    await act(async () => { release() })
    expect(screen.queryByText('Alpha project')).not.toBeInTheDocument()
  })
  it('keeps a write already sent to Alpha there and ignores its late completion in Beta', async () => {
    const state = setup('/companies/1/projects/new'), user = userEvent.setup()
    let release!: () => void
    state.listWait = new Promise<void>(resolve => { release = resolve })
    await user.type(await screen.findByLabelText('Project name'), 'Alpha save')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    await waitFor(() => expect(state.calls.some(c => c.url === '/projects' && c.method === 'post')).toBe(true))
    await user.click(screen.getByRole('button', { name: 'Switch directly to Beta' }))
    expect(await screen.findByLabelText('Project name')).toHaveValue('')
    await act(async () => { release() })
    const writes = state.calls.filter(c => c.url === '/projects' && c.method === 'post')
    expect(writes).toHaveLength(1)
    expect(writes[0].baseURL).toBe('/api/v2/workspaces/1')
    expect(screen.getByTestId('location')).toHaveTextContent('/companies/2/projects/new')
  })
  it('shows an unavailable-company message without loading private resources', async () => {
    const state = setup('/companies/1/projects'); state.unavailable = true
    expect(await screen.findByRole('heading', { name: 'Company not available.' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Choose a company' })).toHaveAttribute('href', '/companies')
    expect(state.calls.filter(c => c.url?.startsWith('/projects'))).toHaveLength(0)
  })
  it('rejects invalid deep links and keeps the preview disabled unless explicitly enabled', async () => {
    const state = setup('/companies/0/projects')
    expect(await screen.findByRole('heading', { name: 'Company not available.' })).toBeInTheDocument()
    expect(state.calls.filter(c => c.baseURL !== '/api/v1')).toHaveLength(0)
  })
  it('does not expose preview routes when the frontend flag is off', async () => {
    setup('/companies', false)
    expect(await screen.findByRole('heading', { name: 'That page is not here.' })).toBeInTheDocument()
    expect(screen.queryByRole('link', { name: 'Companies' })).not.toBeInTheDocument()
  })
})
