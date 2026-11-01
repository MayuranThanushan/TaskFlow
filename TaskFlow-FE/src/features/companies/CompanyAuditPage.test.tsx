import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { afterEach, describe, expect, it, vi } from 'vitest'
import type { InternalAxiosRequestConfig } from 'axios'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'

afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })
function setup(path = '/companies/1/audit', denied = false, ticketWait: Promise<void> | null = null) {
  const auth = server(true), calls: InternalAxiosRequestConfig[] = []
  const client = createApiClient(async c => {
    calls.push(c)
    if (c.baseURL === '/api/v1') return auth.adapter(c)
    if (c.baseURL === '/api/v2' && c.url?.startsWith('/workspaces?')) return reply(c, 200, { items: [], page: 1, pageSize: 20, totalCount: 0, totalPages: 0 })
    if (c.baseURL === '/api/v2') return reply(c, 200, { id: '1', name: 'Acme', role: 'Owner' })
    if (c.url === '/audit/console/exports') { await ticketWait; return reply(c, 200, { ticket: 'protected-ticket', expiresAtUtc: '2026-11-01T01:00:00Z', maximumRecords: 1000 }) }
    if (c.url === '/audit/console/exports/download') return reply(c, denied ? 403 : 200, { generatedAtUtc: '2026-11-01T00:00:00Z', items: [], truncated: true, maximumRecords: 1000 })
    const params = new URLSearchParams(c.url?.split('?')[1])
    return reply(c, 200, { items: [], page: Number(params.get('page') ?? 1), pageSize: 20, totalCount: 40, totalPages: 2, highWatermark: '9007199254741111' })
  })
  render(<MemoryRouter initialEntries={[path]}><App client={client} companiesEnabled lifecycleEnabled /></MemoryRouter>)
  return calls
}
describe('audit console', () => {
  it('applies filters together and preserves them during pagination', async () => {
    const calls = setup(), user = userEvent.setup()
    await screen.findByText('No audit events match these filters.')
    await user.selectOptions(screen.getByLabelText('Event source'), 'Projects')
    await user.selectOptions(screen.getByLabelText('Project state'), 'Deleted')
    await user.type(screen.getByLabelText('Actor ID'), '9007199254741111')
    await user.click(screen.getByRole('button', { name: 'Apply filters' }))
    await waitFor(() => expect(calls.some(c => c.url?.includes('resourceState=Deleted'))).toBe(true))
    await screen.findByText('No audit events match these filters.')
    await user.click(screen.getByRole('button', { name: /Next/ }))
    await waitFor(() => expect(calls.some(c => c.url?.includes('page=2') && c.url.includes('actorId=9007199254741111') && c.url.includes('source=Projects') && c.baseURL === '/api/v2/workspaces/1')).toBe(true))
    expect(screen.getByLabelText('Actor ID')).toHaveValue('9007199254741111')
  })
  it('downloads only applied filters and reports capped results', async () => {
    const calls = setup('/companies/1/audit?source=Projects&resourceState=Deleted'), user = userEvent.setup()
    const NativeUrl = URL
    vi.stubGlobal('URL', class extends NativeUrl { static createObjectURL = vi.fn(() => 'blob:audit'); static revokeObjectURL = vi.fn() })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    await screen.findByText('No audit events match these filters.')
    await user.type(screen.getByLabelText('Action'), 'ProjectDeleted')
    await user.click(screen.getByRole('button', { name: 'Download audit JSON' }))
    expect(await screen.findByRole('status')).toHaveTextContent('More records match')
    const create = calls.find(c => c.url === '/audit/console/exports')!
    expect(JSON.parse(create.data)).toEqual({ filters: { source: 'Projects', resourceState: 'Deleted' } })
    expect(create.headers.get('X-CSRF-TOKEN')).toBeTruthy()
    expect(JSON.parse(calls.find(c => c.url === '/audit/console/exports/download')!.data)).toEqual({ ticket: 'protected-ticket' })
    expect(click).toHaveBeenCalledOnce()
  })
  it('shows revoked download permission and creates no file', async () => {
    setup('/companies/1/audit', true); const user = userEvent.setup()
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    await screen.findByText('No audit events match these filters.')
    await user.click(screen.getByRole('button', { name: 'Download audit JSON' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('permission')
    expect(click).not.toHaveBeenCalled()
    expect(screen.getByRole('button', { name: 'Download audit JSON' })).toBeEnabled()
  })
  it('abandons a pending export before download when leaving the company page', async () => {
    let release!: () => void
    const wait = new Promise<void>(resolve => { release = resolve })
    const calls = setup('/companies/1/audit', false, wait), user = userEvent.setup()
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    await screen.findByText('No audit events match these filters.')
    await user.click(screen.getByRole('button', { name: 'Download audit JSON' }))
    await waitFor(() => expect(calls.some(c => c.url === '/audit/console/exports')).toBe(true))
    await user.click(screen.getByRole('link', { name: 'Companies' }))
    await screen.findByRole('heading', { name: 'Companies' })
    await act(async () => { release(); await wait })
    expect(calls.some(c => c.url === '/audit/console/exports/download')).toBe(false)
    expect(click).not.toHaveBeenCalled()
  })

})
