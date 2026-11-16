import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'
function setup(initial = '/companies/1/work', status = 200, total = 1) {
  const auth = server(true), calls: URL[] = []
  const client = createApiClient(async c => {
    if (c.url?.startsWith('/auth/')) return auth.adapter(c)
    if (c.url === '/workspaces/1') return reply(c, 200, { id: '1', name: 'Studio', role: 'Member' })
    const url = new URL(c.url!, 'https://example.test'); calls.push(url)
    if (url.pathname === '/work') return reply(c, status, { items: total ? [{ task: { id: '101', projectId: '41', title: 'Review launch', status: 'ToDo', priority: 'High', assigneeId: profile.id, dueDateUtc: '2026-11-15T08:00:00Z', createdAtUtc: '2026-11-15T08:00:00Z', updatedAtUtc: '2026-11-15T08:00:00Z', version: '1' }, projectName: 'Product launch', assigneeName: profile.displayName }] : [], page: 1, pageSize: 20, totalCount: total, totalPages: total ? 1 : 0, counts: { open: total, due: 0, overdue: total, completed: 0, total }, asOfUtc: '2026-11-16T08:00:00Z', dueThroughUtc: '2026-11-23T08:00:00Z' })
    return reply(c, 404, {})
  })
  render(<MemoryRouter initialEntries={[initial]}><App client={client} companiesEnabled lifecycleEnabled /></MemoryRouter>)
  return calls
}
describe('company My work', () => {
  it('uses one scoped summary request and links to existing authorized actions', async () => {
    const calls = setup(); expect(await screen.findByRole('link', { name: 'Review launch' })).toHaveAttribute('href', '/companies/1/tasks/101')
    expect(screen.getByRole('link', { name: 'Open board for Product launch' })).toHaveAttribute('href', '/companies/1/projects/41/board')
    expect(screen.getByText('Assigned to you')).toBeInTheDocument(); expect(screen.getByText('Overdue:')).toBeInTheDocument()
    expect(calls.map(c => c.pathname)).toEqual(['/work'])
  })
  it('changes queues while preserving filters and resetting the page', async () => {
    const calls = setup('/companies/1/work?page=4&search=launch'); await screen.findByRole('link', { name: 'Review launch' })
    await userEvent.click(screen.getByRole('button', { name: /Due soon/ }))
    await waitFor(() => expect(calls.at(-1)?.searchParams.get('queue')).toBe('due'))
    expect(calls.at(-1)?.searchParams.has('page')).toBe(false); expect(calls.at(-1)?.searchParams.get('search')).toBe('launch')
  })
  it('applies explicit unassigned semantics and trims search', async () => {
    const calls = setup(); await screen.findByRole('link', { name: 'Review launch' })
    await userEvent.selectOptions(screen.getByLabelText('Show work'), 'unassigned'); await userEvent.type(screen.getByLabelText('Search tasks or projects'), '  launch  ')
    await userEvent.click(screen.getByRole('button', { name: 'Apply filters' }))
    await waitFor(() => expect(calls.at(-1)?.searchParams.get('assignment')).toBe('unassigned')); expect(calls.at(-1)?.searchParams.get('search')).toBe('launch')
  })
  it('shows an actionable empty state', async () => { setup('/companies/1/work', 200, 0); expect(await screen.findByRole('heading', { name: 'No work matches this view.' })).toBeInTheDocument() })
  it('rejects invalid link filters visibly', async () => { setup('/companies/1/work?queue=invalid', 400); expect(await screen.findByRole('button', { name: 'Reset invalid filters' })).toBeInTheDocument(); expect(screen.queryByRole('link', { name: 'Review launch' })).not.toBeInTheDocument() })
  it('does not display inaccessible results', async () => { setup('/companies/1/work?projectId=99', 404); expect(await screen.findByRole('heading', { name: 'Company or project not available.' })).toBeInTheDocument(); expect(screen.queryByRole('link', { name: 'Review launch' })).not.toBeInTheDocument() })
})
