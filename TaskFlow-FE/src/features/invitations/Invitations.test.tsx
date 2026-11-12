import { StrictMode } from 'react'
import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'
const secret = 's'.repeat(43)
const link = '/invitations/accept#workspaceId=1&invitationId=5&token=' + secret
function Location() { const l = useLocation(); return <div data-testid="location">{l.pathname + l.search + l.hash}</div> }
const row = { id: '5', workspaceId: '1', projectId: null, email: 'invitee@example.test', role: 'Member', state: 'Pending', version: '9007199254741111', expiresAtUtc: '2030-01-01T00:00:00Z', resendAvailableAtUtc: '2020-01-01T00:00:00Z', resends: 0, deliveryState: 'Captured' }
function setup(path = link, authenticated = true, options: { role?: string; verified?: boolean; previewStatus?: number; enabled?: boolean; strict?: boolean } = {}) {
  const auth = server(authenticated), state = { role: options.role ?? 'Owner', verified: options.verified ?? true, previewStatus: options.previewStatus ?? 200, acceptStatus: 200, status: 200, code: 'invitation.conflict', rows: [{ ...row }], posts: [] as { url: string; base: string; data: Record<string, string> }[], previewCalls: 0, wait: null as Promise<void> | null }
  const client = createApiClient(async c => {
    if (c.url === '/auth/security') return reply(c, 200, { emailConfirmed: state.verified, reauthenticatedUntilUtc: null })
    if (c.baseURL === '/api/v1') return auth.adapter(c)
    if (c.baseURL === '/api/v2') return reply(c, 200, { id: '1', name: 'Acme', role: state.role })
    if (c.url === '/policy') return reply(c, 200, { capacity: { activeMembers: 2, reservedMemberSeats: 1, memberLimit: 25 } })
    if (c.url === '/projects/9') return reply(c, 200, { id: '9', name: 'Private plan', ownerId: profile.id })
    if (c.url?.endsWith('/preview')) { state.previewCalls++; return reply(c, state.previewStatus, { workspaceId: '1', workspaceName: 'Acme', projectId: '9', projectName: 'Private plan', role: 'Viewer', expiresAtUtc: row.expiresAtUtc }) }
    if (c.method === 'get' && c.url?.startsWith('/invitations')) return reply(c, 200, { items: state.rows, page: 1, pageSize: 20, totalCount: state.rows.length, totalPages: state.rows.length ? 1 : 0 })
    if (c.method === 'post' && c.url?.startsWith('/invitations')) {
      const data = JSON.parse(c.data as string) as Record<string, string>; state.posts.push({ url: c.url, base: c.baseURL!, data }); await state.wait
      if (c.url.endsWith('/accept')) return reply(c, state.acceptStatus, {})
      if (state.status !== 200) return reply(c, state.status, { code: state.code })
      if (c.url.endsWith('/revoke')) state.rows = state.rows.map(r => ({ ...r, state: 'Revoked' }))
      return reply(c, c.url === '/invitations' ? 201 : 200, { ...row })
    }
    return reply(c, 404)
  })
  const app = <MemoryRouter initialEntries={[path]}><App client={client} companiesEnabled lifecycleEnabled invitationsEnabled={options.enabled ?? true} /><Location /></MemoryRouter>
  render(options.strict ? <StrictMode>{app}</StrictMode> : app)
  return { state, auth, user: userEvent.setup() }
}
describe('invitation recipient', () => {
  it('clears the fragment and preserves the invitation through sign-in without storage or automatic acceptance', async () => {
    const { state, user } = setup(link, false, { strict: true })
    await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/invitations\/accept$/))
    expect(screen.getByRole('heading', { name: 'Join your team' })).toBeVisible(); expect(state.previewCalls).toBe(0)
    await user.click(screen.getByRole('link', { name: 'Sign in to review' })); await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })
    expect(document.body.textContent).not.toContain(secret); expect(screen.getByTestId('location').textContent).not.toContain(secret)
    await user.type(screen.getByLabelText('Email address'), profile.email); await user.type(screen.getByLabelText('Password'), 'CorrectPassword123!'); await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByText('Private plan')).toBeVisible(); expect(state.posts).toHaveLength(0)
    expect(localStorage.length).toBe(0); expect(sessionStorage.length).toBe(0)
  })
  it('keeps an invitation through signup then requires explicit acceptance', async () => {
    const { state, user } = setup(link, false)
    await user.click(await screen.findByRole('link', { name: 'Create an account' })); await user.type(screen.getByLabelText('Your name'), 'Alex'); await user.type(screen.getByLabelText('Email address'), profile.email); await user.type(screen.getByLabelText('Password'), 'CorrectPassword123!'); await user.click(screen.getByRole('button', { name: 'Create account' }))
    await screen.findByRole('heading', { name: 'Sign in to TaskFlow' }); await user.type(screen.getByLabelText('Password'), 'CorrectPassword123!'); await user.click(screen.getByRole('button', { name: 'Sign in' })); expect(await screen.findByText('Private plan')).toBeVisible(); expect(state.posts).toHaveLength(0)
  })
  it('requires verified email before requesting private preview', async () => {
    const { state } = setup(link, true, { verified: false }); expect(await screen.findByText('Verify your account email before accepting this invitation.')).toBeVisible(); expect(state.previewCalls).toBe(0); expect(screen.getByRole('link', { name: 'Verify your email' })).toHaveAttribute('href', '/request-verification'); expect(screen.queryByRole('button', { name: 'Accept invitation' })).not.toBeInTheDocument()
  })
  it('offers safe wrong-account/expired-link recovery without disclosing the private scope', async () => {
    const { user } = setup(link, true, { previewStatus: 404 }); expect(await screen.findByRole('alert')).toHaveTextContent('unavailable for this account'); expect(screen.queryByText('Private plan')).not.toBeInTheDocument(); await user.click(screen.getByRole('button', { name: 'Use another account' })); expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(document.body.textContent).not.toContain(secret)
  })
  it('posts the body token once only after confirmation and waits for success before showing access', async () => {
    const { state, user } = setup(); await screen.findByText('Private plan'); let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve })
    await user.dblClick(screen.getByRole('button', { name: 'Accept invitation' })); expect(state.posts).toHaveLength(1); expect(screen.queryByRole('link', { name: 'Open project' })).not.toBeInTheDocument()
    await act(async () => release()); expect(await screen.findByRole('heading', { name: 'Invitation accepted' })).toBeVisible(); expect(screen.getByRole('link', { name: 'Open project' })).toHaveAttribute('href', '/companies/1/projects/9'); expect(state.posts[0]).toEqual({ url: '/invitations/5/accept', base: '/api/v2/workspaces/1', data: { token: secret } })
  })
  it('handles missing and invalid fragments without a preview request', async () => {
    const { state } = setup('/invitations/accept#workspaceId=1&invitationId=5&token=' + secret + '&token=' + secret)
    expect(await screen.findByRole('heading', { name: 'Open your invitation' })).toBeVisible(); expect(state.previewCalls).toBe(0); await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/invitations\/accept$/))
  })
  it('keeps the rollout default off and strips a token even on an unavailable deployment', async () => {
    const { state } = setup(link, true, { enabled: false }); expect(await screen.findByRole('heading', { name: 'Invitations unavailable' })).toBeVisible(); await waitFor(() => expect(screen.getByTestId('location')).toHaveTextContent(/^\/invitations\/accept$/)); expect(state.previewCalls).toBe(0)
  })
})
describe('invitation management', () => {
  it('shows capacity, limits roles and creates only a trimmed recipient request', async () => {
    const { state, user } = setup('/companies/1/invitations'); await screen.findByText('invitee@example.test'); expect(screen.getByText('1 reserved seat')).toBeVisible(); expect(screen.getByText('22 seats available')).toBeVisible()
    expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Member', 'Guest']); await user.type(screen.getByLabelText('Recipient email'), '  next@example.test  '); await user.selectOptions(screen.getByLabelText('Role'), 'Guest'); await user.click(screen.getByRole('button', { name: 'Create invitation' }))
    expect(await screen.findByText(/Invitation created and queued/)).toBeVisible(); expect(state.posts[0]).toMatchObject({ url: '/invitations', base: '/api/v2/workspaces/1', data: { email: 'next@example.test', role: 'Guest' } }); expect(screen.getByLabelText('Recipient email')).toHaveValue('')
  })
  it('requires confirmation, supports cancellation, and posts the displayed revision', async () => {
    const { state, user } = setup('/companies/1/invitations'); await user.click(await screen.findByRole('button', { name: 'Revoke for invitee@example.test' })); expect(state.posts).toHaveLength(0); expect(screen.getByRole('button', { name: 'Keep invitation' })).toHaveFocus(); await user.keyboard('{Escape}'); expect(screen.queryByRole('button', { name: 'Revoke invitation' })).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Resend to invitee@example.test' })); await user.click(screen.getByRole('button', { name: 'Resend invitation' })); expect(state.posts[0].data).toEqual({ expectedVersion: row.version }); expect(await screen.findByText(/previous link no longer works/)).toBeVisible()
    await user.click(screen.getByRole('button', { name: 'Revoke for invitee@example.test' })); await user.click(screen.getByRole('button', { name: 'Revoke invitation' })); expect(await screen.findByText(/Invitation revoked/)).toBeVisible(); expect(screen.queryByRole('button', { name: 'Revoke for invitee@example.test' })).not.toBeInTheDocument()
  })
  it('retains a failed creation draft and does not retry a stale action automatically', async () => {
    const { state, user } = setup('/companies/1/invitations'); await screen.findByText('invitee@example.test'); state.status = 409
    await user.type(screen.getByLabelText('Recipient email'), 'keep@example.test'); await user.click(screen.getByRole('button', { name: 'Create invitation' })); expect(await screen.findByRole('alert')).toHaveTextContent('invitation changed'); expect(screen.getByLabelText('Recipient email')).toHaveValue('keep@example.test'); expect(state.posts).toHaveLength(1)
  })
  it('explains the shared hourly send limit, keeps the draft and never retries automatically', async () => {
    const { state, user } = setup('/companies/1/invitations'); await screen.findByText('invitee@example.test'); state.status = 429; state.code = 'invitation.send_limited'
    await user.type(screen.getByLabelText('Recipient email'), 'keep@example.test'); await user.click(screen.getByRole('button', { name: 'Create invitation' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('hourly invitation send limit'); expect(screen.getByRole('alert')).toHaveTextContent('resends count toward the same limit')
    expect(screen.getByLabelText('Recipient email')).toHaveValue('keep@example.test'); expect(state.posts).toHaveLength(1)
  })
  it('hides private records immediately after losing permission on a write', async () => {
    const { state, user } = setup('/companies/1/invitations'); await screen.findByText('invitee@example.test'); state.status = 403; await user.click(screen.getByRole('button', { name: 'Revoke for invitee@example.test' })); await user.click(screen.getByRole('button', { name: 'Revoke invitation' })); await screen.findByRole('alert'); await waitFor(() => expect(screen.queryByText('invitee@example.test')).not.toBeInTheDocument()); expect(screen.queryByLabelText('Recipient email')).not.toBeInTheDocument()
  })
  it('requires company administration and current project ownership and uses project roles', async () => {
    const { user, state } = setup('/companies/1/projects/9/invitations'); await screen.findByText('invitee@example.test'); expect(screen.getAllByRole('option').map(o => o.textContent)).toEqual(['Viewer', 'Editor']); await user.type(screen.getByLabelText('Recipient email'), 'project@example.test'); await user.click(screen.getByRole('button', { name: 'Create invitation' })); await screen.findByText(/Invitation created and queued/); expect(state.posts[0].data).toMatchObject({ projectId: '9', role: 'Viewer' })
  })
  it('enforces resend cooldown and the bounded resend count in the controls', async () => {
    const { state } = setup('/companies/1/invitations'); state.rows[0].resendAvailableAtUtc = '2099-01-01T00:00:00Z'; state.rows[0].resends = 3
    expect(await screen.findByRole('button', { name: 'Resend to invitee@example.test' })).toBeDisabled(); expect(screen.getByText('Resend limit reached')).toBeVisible(); expect(state.posts).toHaveLength(0)
  })
  it('distinguishes provider acceptance from confirmed delivery and local previews', async () => {
    const { state } = setup('/companies/1/invitations'); state.rows[0].deliveryState = 'ProviderAccepted'
    expect(await screen.findByText('Delivery: Accepted by email provider (delivery not confirmed)')).toBeVisible()
    expect(screen.queryByText(/Preview captured/)).not.toBeInTheDocument()
  })
  it('does not fetch invitation recipients for non-admin company members', async () => {
    const { state } = setup('/companies/1/invitations', true, { role: 'Member' }); expect(await screen.findByRole('heading', { name: 'Company administration required' })).toBeVisible(); expect(screen.queryByLabelText('Recipient email')).not.toBeInTheDocument(); expect(state.posts).toHaveLength(0)
  })
})
