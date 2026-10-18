import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'
import type { ActivityItem } from './ActivityPage'
const projectId = '9007199254741111'; const path = '/projects/' + projectId + '/activity'
const event = (details = 'Task created.', id = '9007199254741999'): ActivityItem => ({ id, projectId, actorUserId: '9007199254741888', taskId: '9007199254741777', actionType: 'TaskCreated', details, createdAtUtc: '2026-10-18T08:00:00Z' })
function Location() { const location = useLocation(); const navigate = useNavigate(); return <><span data-testid="location">{location.pathname + location.search}</span><button onClick={() => void navigate(-1)}>Browser back</button></> }
function setup(route = path, events: ActivityItem[] = [], authenticated = true) {
  const auth = server(authenticated)
  const state = { events, status: 200, queries: [] as string[], currentProfile: { ...profile }, wait: null as Promise<void> | null, writes: 0 }
  const client = createApiClient(async config => {
    if (config.url === '/auth/me') return reply(config, auth.state.authenticated ? 200 : 401, state.currentProfile)
    if (!config.url?.startsWith('/projects/')) return auth.adapter(config)
    if (config.method !== 'get') state.writes++
    const url = new URL(config.url, 'https://taskflow.test'); state.queries.push(url.search); const rows = [...state.events]; const status = state.status; await state.wait
    if (status !== 200) return reply(config, status)
    const from = url.searchParams.get('fromUtc'); const to = url.searchParams.get('toUtc'); const items = rows.filter(row => (!from || new Date(row.createdAtUtc) >= new Date(from)) && (!to || new Date(row.createdAtUtc) <= new Date(to)))
    const page = Number(url.searchParams.get('page') ?? 1); const pageSize = Number(url.searchParams.get('pageSize') ?? 20)
    return reply(config, 200, { items: items.slice((page - 1) * pageSize, page * pageSize), page, pageSize, totalCount: items.length, totalPages: Math.ceil(items.length / pageSize) })
  })
  render(<StrictMode><MemoryRouter initialEntries={[route]}><App client={client} /><Location /></MemoryRouter></StrictMode>)
  return state
}
describe('Project activity', () => {
  it('protects the feed and renders safe summaries, opaque IDs and local timestamps without write controls', async () => {
    const state = setup(path, [event('<img src=x onerror=alert(1)>')]); expect(await screen.findByRole('heading', { name: '<img src=x onerror=alert(1)>' })).toBeVisible(); expect(document.querySelector('.activity-list img')).toBeNull(); expect(screen.getByText(/User ID: 9007199254741888/)).toBeVisible(); expect(screen.getByText(/Task ID: 9007199254741777/)).toBeVisible(); expect(document.querySelector('time')).toHaveAttribute('datetime', '2026-10-18T08:00:00Z'); expect(state.writes).toBe(0)
  })
  it('sends no feed request before authentication', async () => {
    const state = setup(path, [], false); expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(state.queries).toHaveLength(0)
  })
  it('converts local ranges to UTC, preserves paging/history and resets the range', async () => {
    const rows = Array.from({ length: 23 }, (_, i) => event('Event ' + i, String(i + 1))); const state = setup(path, rows); const user = userEvent.setup(); await screen.findByText('23 events'); await user.selectOptions(screen.getByLabelText('Per page'), '10'); await user.click(screen.getByRole('button', { name: 'Apply' })); expect(await screen.findByText('Page 1 of 3')).toBeVisible(); await user.click(screen.getByRole('button', { name: 'Next' })); expect(await screen.findByText('Page 2 of 3')).toBeVisible(); await user.click(screen.getByRole('button', { name: 'Browser back' })); expect(await screen.findByText('Page 1 of 3')).toBeVisible()
    fireEvent.change(screen.getByLabelText('From date and time (optional)'), { target: { value: '2026-10-18T00:00' } }); fireEvent.change(screen.getByLabelText('To date and time (optional)'), { target: { value: '2026-10-19T00:00' } }); await user.click(screen.getByRole('button', { name: 'Apply' })); await waitFor(() => expect(new URLSearchParams(state.queries.at(-1)).get('fromUtc')).toBe(new Date('2026-10-18T00:00').toISOString())); expect(new URLSearchParams(state.queries.at(-1)).get('toUtc')).toBe(new Date('2026-10-19T00:00').toISOString()); expect(new URLSearchParams(state.queries.at(-1)).has('page')).toBe(false)
    await user.click(screen.getByRole('button', { name: 'Reset' })); await screen.findByText('23 events'); expect(screen.getByLabelText('From date and time (optional)')).toHaveValue(''); expect(state.queries.at(-1)).toBe('')
  })
  it('keeps precise bookmarked bounds unchanged, validates reversed local ranges and recovers empty or invalid ranges', async () => {
    const from = '2026-10-18T08:00:00.1234567Z'; const state = setup(path + '?fromUtc=' + encodeURIComponent(from), []); const user = userEvent.setup(); expect(await screen.findByRole('heading', { name: 'No activity in this range.' })).toBeVisible(); await user.click(screen.getByRole('button', { name: 'Apply' })); await waitFor(() => expect(state.queries.length).toBeGreaterThan(1)); expect(new URLSearchParams(state.queries.at(-1)).get('fromUtc')).toBe(from)
    const previous = state.queries.length; fireEvent.change(screen.getByLabelText('From date and time (optional)'), { target: { value: '2026-11-02T12:00' } }); fireEvent.change(screen.getByLabelText('To date and time (optional)'), { target: { value: '2026-11-01T12:00' } }); await user.click(screen.getByRole('button', { name: 'Apply' })); expect(await screen.findByRole('alert')).toHaveTextContent('start must not be later'); expect(state.queries).toHaveLength(previous)
  })
  it('offers safe server retry, invalid-filter reset and a fresh feed refresh', async () => {
    const state = setup(path + '?bad=1'); state.status = 400; const user = userEvent.setup(); expect(await screen.findByRole('alert')).toHaveTextContent('activity filters are invalid'); state.status = 503; await user.click(screen.getByRole('button', { name: 'Reset invalid filters' })); expect(await screen.findByRole('button', { name: 'Try again' })).toBeVisible(); state.status = 200; state.events = [event()]; await user.click(screen.getByRole('button', { name: 'Try again' })); expect(await screen.findByRole('heading', { name: 'Task created.' })).toBeVisible(); state.events = [event('Task deleted.')]; await user.click(screen.getByRole('button', { name: 'Refresh activity' })); expect(await screen.findByRole('heading', { name: 'Task deleted.' })).toBeVisible()
  })
  it('handles out-of-range pages and clears prior-account history while ignoring late results', async () => {
    const state = setup(path + '?page=9&pageSize=10', [event()]); const user = userEvent.setup(); expect(await screen.findByRole('heading', { name: 'No activity on this page.' })).toBeVisible(); await user.click(screen.getByRole('button', { name: 'Go to first page' })); expect(await screen.findByRole('heading', { name: 'Task created.' })).toBeVisible()
    let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve }); await user.click(screen.getByRole('button', { name: 'Refresh activity' })); state.currentProfile = { ...profile, id: 'other', displayName: 'Other' }; state.status = 404; state.wait = null; fireEvent(window, new Event('focus')); expect(await screen.findByRole('heading', { name: 'Project not available.' })).toBeVisible(); await act(async () => { release() }); expect(screen.queryByRole('heading', { name: 'Task created.' })).not.toBeInTheDocument(); expect(screen.getByRole('button', { name: 'Sign out' })).toBeVisible()
  })
})
