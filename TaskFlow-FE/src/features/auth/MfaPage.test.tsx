import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'

const syntheticKey = 'LOCALTESTKEYONLY'
const syntheticCodes = Array.from({ length: 10 }, (_, i) => 'A'.repeat(31) + i)
function setup({ enabled = false, available = true, required = false, authenticated = true, recovering = false } = {}) {
  const auth = server(authenticated), calls: { path: string; body: Record<string, string> }[] = []
  const state = { enabled, available, required, failure: '', wait: null as Promise<void> | null }
  const client = createApiClient(async config => {
    if (config.url === '/auth/mfa') return reply(config, 200, { enabled: state.enabled, enrollmentAvailable: state.available, recoveryCodesRemaining: state.enabled ? 10 : 0, requiredForAdministration: state.required, verifiedUntilUtc: null, recoveryVerifiedUntilUtc: recovering ? new Date(Date.now() + 300000).toISOString() : null })
    if (config.url?.startsWith('/auth/mfa/')) {
      calls.push({ path: config.url, body: JSON.parse(config.data) }); await state.wait
      if (state.failure) return reply(config, 400, { code: state.failure })
      if (config.url.endsWith('/setup')) return reply(config, 200, { sharedKey: syntheticKey, expiresAtUtc: new Date(Date.now() + 600000).toISOString() })
      if (config.url.endsWith('/disable')) { auth.state.authenticated = false; return reply(config, 204) }
      state.enabled = true; return reply(config, 200, { recoveryCodes: syntheticCodes })
    }
    return auth.adapter(config)
  })
  render(<MemoryRouter initialEntries={['/account-security/mfa']}><App client={client} /></MemoryRouter>)
  return { state, calls, auth }
}
async function password(user: ReturnType<typeof userEvent.setup>) { await user.type(await screen.findByLabelText('Current password'), 'ExampleOnly-Pass123!') }
describe('MFA management', () => {
  it('lets a fresh recovery session restore with its password without spending another code', async () => {
    const user = userEvent.setup(), { calls } = setup({ enabled: true, required: true, recovering: true })
    await password(user); await user.click(screen.getByRole('button', { name: 'Replace authenticator' }))
    expect(await screen.findByText(syntheticKey)).toBeVisible(); expect(calls[0].body).toEqual({ currentPassword: 'ExampleOnly-Pass123!' })
  })
  it('protects the page and keeps disabled enrollment closed', async () => {
    setup({ available: false }); expect(await screen.findByText('New MFA enrollment is disabled. Contact your operations team.')).toBeVisible()
    expect(screen.queryByLabelText('Current password')).not.toBeInTheDocument()
  })
  it('requires enrollment proof, confirms once and hides one-time recovery codes after acknowledgement', async () => {
    const user = userEvent.setup(), { calls, state } = setup()
    await password(user); await user.click(screen.getByRole('button', { name: 'Set up authenticator' }))
    expect(await screen.findByText(syntheticKey)).toBeVisible(); expect(screen.getByLabelText('Current password')).toHaveValue('')
    expect(screen.queryByText('Save your recovery codes')).not.toBeInTheDocument()
    await password(user); await user.type(screen.getByLabelText('Authenticator code'), '123456')
    let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve })
    await user.dblClick(screen.getByRole('button', { name: 'Confirm authenticator' }))
    expect(calls.filter(c => c.path.endsWith('/confirm'))).toHaveLength(1); expect(screen.queryByText('Save your recovery codes')).not.toBeInTheDocument()
    release(); expect(await screen.findByRole('heading', { name: 'Save your recovery codes' })).toBeVisible()
    expect(screen.queryByText(syntheticKey)).not.toBeInTheDocument(); expect(screen.getByRole('list', { name: 'Recovery codes' }).children).toHaveLength(10)
    expect(JSON.stringify(localStorage)).not.toContain(syntheticKey); expect(JSON.stringify(sessionStorage)).not.toContain(syntheticCodes[0])
    await user.click(screen.getByRole('button', { name: 'I saved my recovery codes' })); expect(screen.queryByRole('list', { name: 'Recovery codes' })).not.toBeInTheDocument()
    expect(await screen.findByText('Enabled')).toBeVisible()
  })
  it('supports recovery for authenticator restoration without sending both factors', async () => {
    const user = userEvent.setup(), { calls } = setup({ enabled: true, required: true })
    await password(user); expect(screen.getByRole('button', { name: 'Disable MFA' })).toBeDisabled()
    await user.click(screen.getByLabelText('Use a recovery code to restore or disable MFA'))
    await user.type(screen.getByLabelText('Recovery code'), syntheticCodes[0]); await user.click(screen.getByRole('button', { name: 'Replace authenticator' }))
    await screen.findByText(syntheticKey)
    expect(calls[0].body.recoveryCode).toBe(syntheticCodes[0]); expect(calls[0].body).not.toHaveProperty('code')
    expect(screen.queryByLabelText('Recovery code')).not.toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Cancel setup' })); expect(screen.queryByText(syntheticKey)).not.toBeInTheDocument()
  })
  it('confirms destructive actions, supports Escape, and clears proof after rejected writes', async () => {
    const user = userEvent.setup(), { state, calls } = setup({ enabled: true })
    await password(user); await user.type(screen.getByLabelText('Authenticator code'), '123456')
    await user.click(screen.getByRole('button', { name: 'Replace recovery codes' })); await user.keyboard('{Escape}'); expect(calls).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Replace recovery codes' })); state.failure = 'mfa.proof_invalid'
    await user.click(screen.getByRole('button', { name: 'Replace codes' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Proof could not be verified')
    expect(screen.getByLabelText('Current password')).toHaveValue(''); expect(screen.getByLabelText('Authenticator code')).toHaveValue(''); expect(screen.queryByRole('list', { name: 'Recovery codes' })).not.toBeInTheDocument()
  })
  it('redirects anonymous visitors to sign-in before requesting credential metadata', async () => {
    const { calls } = setup({ authenticated: false })
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible(); expect(calls).toHaveLength(0)
  })
  it('submits the selected sign-in factor and clears its value after failure', async () => {
    const user = userEvent.setup(), auth = server(); auth.state.loginStatus = 401
    render(<MemoryRouter initialEntries={['/login']}><App client={createApiClient(auth.adapter)} /></MemoryRouter>)
    await user.type(await screen.findByLabelText('Email address'), 'alex@example.test'); await user.type(screen.getByLabelText('Password'), 'ExampleOnly-Pass123!')
    await user.selectOptions(screen.getByLabelText('Additional verification'), 'recovery'); await user.type(screen.getByLabelText('Recovery code'), syntheticCodes[0]); await user.click(screen.getByRole('button', { name: 'Sign in' }))
    await screen.findByRole('alert'); await waitFor(() => expect(screen.getByLabelText('Recovery code')).toHaveValue(''))
    const login = auth.state.calls.find(c => c.url === '/auth/login')!; expect(JSON.parse(login.data)).toMatchObject({ recoveryCode: syntheticCodes[0], password: 'ExampleOnly-Pass123!' }); expect(JSON.parse(login.data)).not.toHaveProperty('code')
  })
})
