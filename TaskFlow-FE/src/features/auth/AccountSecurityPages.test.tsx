import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'
function Probe() { const location = useLocation(); return <div data-testid="route">{location.pathname + location.hash}</div> }
function setup(path: string, authenticated = false, enabled = true) {
  const auth = server(authenticated), calls: { url: string; body: Record<string, string> }[] = []
  const state = { status: 204, recent: false }
  const client = createApiClient(async config => {
    if (config.url === '/auth/capabilities') return reply(config, 200, { emailFlowsEnabled: enabled, requireConfirmedEmail: false })
    if (config.url === '/auth/security') return reply(config, 200, { emailConfirmed: false, reauthenticatedUntilUtc: state.recent ? new Date(Date.now() + 300000).toISOString() : null })
    if (['/auth/forgot-password', '/auth/request-verification', '/auth/confirm-email', '/auth/reset-password', '/auth/change-password', '/auth/reauthenticate'].includes(config.url ?? '')) {
      calls.push({ url: config.url!, body: JSON.parse(config.data as string) as Record<string, string> })
      if (config.url === '/auth/reauthenticate') state.recent = true
      if (['/auth/reset-password', '/auth/change-password', '/auth/confirm-email'].includes(config.url!)) auth.state.authenticated = false
      return reply(config, config.url?.includes('forgot') || config.url?.includes('request-') ? 202 : state.status, state.status === 400 ? { errors: { token: ['Invalid token'] } } : {})
    }
    return auth.adapter(config)
  })
  render(<MemoryRouter initialEntries={[path]}><App client={client} /><Probe /></MemoryRouter>)
  return { calls, state, user: userEvent.setup() }
}
describe('account security journeys', () => {
  it('validates recovery and gives a generic receipt without echoing an account', async () => {
    const { calls, user } = setup('/forgot-password')
    await waitFor(() => expect(screen.getByRole('button', { name: 'Request link' })).toBeEnabled())
    await user.click(screen.getByRole('button', { name: 'Request link' })); expect(screen.getByLabelText('Email address')).toHaveFocus(); expect(calls).toHaveLength(0)
    await user.type(screen.getByLabelText('Email address'), 'unknown@example.test'); await user.click(screen.getByRole('button', { name: 'Request link' }))
    expect(await screen.findByRole('status')).toHaveTextContent('If the address is eligible')
    expect(calls).toEqual([{ url: '/auth/forgot-password', body: { email: 'unknown@example.test' } }])
  })
  it('removes fragment secrets and requires explicit confirmation before posting', async () => {
    const token = 'private-preview-token-1234567890', { calls, user } = setup('/verify-email#userId=42&token=' + token)
    await waitFor(() => expect(screen.getByTestId('route')).toHaveTextContent(/^\/verify-email$/))
    expect(calls).toHaveLength(0); expect(document.body.textContent).not.toContain(token)
    await waitFor(() => expect(screen.getByRole('button', { name: 'Verify email' })).toBeEnabled()); await user.click(screen.getByRole('button', { name: 'Verify email' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Email verified')
    expect(calls[0]).toEqual({ url: '/auth/confirm-email', body: { userId: '42', token } }); expect(localStorage.length).toBe(0); expect(sessionStorage.length).toBe(0)
  })
  it('rejects mismatched passwords and supports a safe expired-link retry path', async () => {
    const { calls, state, user } = setup('/reset-password#userId=42&token=private-preview-token-1234567890'); state.status = 400
    await waitFor(() => expect(screen.getByRole('button', { name: 'Reset password' })).toBeEnabled())
    await user.type(screen.getByLabelText('New password'), 'Changed-Password123!'); await user.click(screen.getByRole('button', { name: 'Reset password' }))
    expect(screen.getByLabelText('Confirm new password')).toHaveFocus(); expect(calls).toHaveLength(0)
    await user.type(screen.getByLabelText('Confirm new password'), 'Changed-Password123!'); await user.click(screen.getByRole('button', { name: 'Reset password' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('invalid or expired'); expect(screen.getByRole('link', { name: 'Request a new link' })).toHaveAttribute('href', '/forgot-password')
  })
  it('shows unavailable mail service without permitting a submission', async () => {
    setup('/request-verification', false, false)
    expect(await screen.findByRole('status')).toHaveTextContent('currently unavailable'); expect(screen.queryByRole('button', { name: 'Request link' })).not.toBeInTheDocument()
  })
  it('confirms identity using only the current password and clears its input', async () => {
    const { calls, user } = setup('/account-security', true)
    const current = await screen.findByLabelText('Current password'); await user.type(current, 'Correct-Password123!'); await user.click(screen.getByRole('button', { name: 'Confirm identity only' }))
    expect(await screen.findByRole('status')).toHaveTextContent('Identity confirmed'); expect(current).toHaveValue('')
    expect(calls).toEqual([{ url: '/auth/reauthenticate', body: { currentPassword: 'Correct-Password123!' } }])
  })
  it('protects account security behind sign in', async () => {
    setup('/account-security'); expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(screen.queryByLabelText('Current password')).not.toBeInTheDocument()
  })
})
