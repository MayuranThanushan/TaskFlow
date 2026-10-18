import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from './App'
import { createApiClient } from './api/client'
import { server } from './test/server'

function Location() { const location = useLocation(); return <span data-testid="location">{location.pathname + location.search + location.hash}</span> }
function setup(path = '/login', authenticated = false, state?: object) {
  const mock = server(authenticated)
  render(<StrictMode><MemoryRouter initialEntries={[{ pathname: path, state }]}><App client={createApiClient(mock.adapter)} /><Location /></MemoryRouter></StrictMode>)
  return mock.state
}
async function credentials() {
  const user = userEvent.setup()
  await user.type(await screen.findByLabelText('Email address'), 'alex@example.com')
  await user.type(screen.getByLabelText('Password'), 'CorrectPassword123!')
  return user
}
describe('Authentication navigation', () => {
  it('redirects anonymous protected visits and returns after sign-in', async () => {
    setup('/dashboard')
    const user = await credentials()
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
    expect(screen.getByTestId('location')).toHaveTextContent('/dashboard')
  })
  it('restores a signed-in session and keeps guest pages out of authenticated navigation', async () => {
    setup('/register', true)
    expect(await screen.findByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
    expect(screen.queryByLabelText('Password')).not.toBeInTheDocument()
  })
  it('validates fields before sending registration', async () => {
    const state = setup('/register')
    await screen.findByLabelText('Your name')
    await userEvent.click(screen.getByRole('button', { name: 'Create account' }))
    expect(screen.getByLabelText('Your name')).toHaveFocus()
    expect(screen.getByLabelText('Password')).toHaveAttribute('aria-invalid', 'true')
    expect(state.calls.filter(call => call.method === 'post')).toHaveLength(0)
  })
  it('registers then asks for sign-in without carrying the password', async () => {
    const state = setup('/register')
    const user = await credentials()
    await user.type(screen.getByLabelText('Your name'), ' Alex ')
    await user.click(screen.getByRole('button', { name: 'Create account' }))
    expect(await screen.findByText('Account created. Sign in to continue.')).toBeVisible()
    expect(screen.getByLabelText('Email address')).toHaveValue('alex@example.com')
    expect(screen.getByLabelText('Password')).toHaveValue('')
    expect(state.authenticated).toBe(false)
    const registration = state.calls.find(call => call.url === '/auth/register')!
    expect(JSON.parse(registration.data)).toMatchObject({ displayName: 'Alex', password: 'CorrectPassword123!' })
  })
  it('shows safe login errors and focuses their summary', async () => {
    const state = setup(); state.loginStatus = 401
    const user = await credentials()
    await user.click(screen.getByRole('button', { name: 'Sign in' }))
    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('Email or password is incorrect')
    await waitFor(() => expect(alert).toHaveFocus())
    expect(screen.getByLabelText('Password')).toHaveValue('CorrectPassword123!')
  })
  it('shows rate limiting without retrying login', async () => {
    const state = setup(); state.loginStatus = 429
    const user = await credentials(); await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Too many attempts')
    expect(state.calls.filter(call => call.url === '/auth/login')).toHaveLength(1)
  })
  it('signs out and protects the workspace again', async () => {
    setup('/dashboard', true)
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }))
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible()
    expect(screen.queryByText('Welcome back, Alex.')).not.toBeInTheDocument()
  })
  it('keeps the user signed in when logout fails', async () => {
    const state = setup('/dashboard', true); state.logoutStatus = 500
    await userEvent.click(await screen.findByRole('button', { name: 'Sign out' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('could not sign you out')
    expect(screen.getByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
  })
  it('expires a restored session on a failed focus recheck', async () => {
    const state = setup('/dashboard', true)
    await screen.findByRole('heading', { name: 'Welcome back, Alex.' })
    state.authenticated = false
    await act(async () => { fireEvent.focus(window) })
    expect(await screen.findByText('Your session has expired. Sign in again.')).toBeVisible()
    expect(screen.getByTestId('location')).toHaveTextContent('/login')
  })
  it('offers retry when the initial session check fails', async () => {
    const state = setup('/dashboard'); state.meStatus = 503
    expect(await screen.findByRole('alert')).toHaveTextContent('could not check your session')
    state.meStatus = 0; state.authenticated = true
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
  })
  it('keeps private content hidden while the session is being checked', async () => {
    const state = setup('/dashboard', true)
    let release!: () => void
    state.meWait = new Promise<void>(resolve => { release = resolve })
    expect(screen.getByRole('status')).toHaveTextContent('Checking your session')
    expect(screen.queryByText('Welcome back, Alex.')).not.toBeInTheDocument()
    await act(async () => { release() })
    expect(await screen.findByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
  })
  it('prevents duplicate submits while login is pending', async () => {
    const state = setup()
    let release!: () => void
    state.loginWait = new Promise<void>(resolve => { release = resolve })
    const user = await credentials()
    await user.dblClick(screen.getByRole('button', { name: 'Sign in' }))
    expect(screen.getByLabelText('Password')).toBeDisabled()
    expect(state.calls.filter(call => call.url === '/auth/login')).toHaveLength(1)
    await act(async () => { release() })
    expect(await screen.findByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
  })
  it('retains the session on a temporary recheck failure', async () => {
    const state = setup('/dashboard', true)
    await screen.findByRole('heading', { name: 'Welcome back, Alex.' })
    state.meStatus = 503
    await act(async () => { fireEvent.focus(window) })
    expect(await screen.findByRole('status')).toHaveTextContent('could not check your session')
    expect(screen.getByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
    state.meStatus = 0
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    await waitFor(() => expect(screen.queryByRole('status')).not.toBeInTheDocument())
  })
  it('uses the dashboard for an unsafe return target', async () => {
    setup('/login', false, { returnTo: '//evil.test' })
    const user = await credentials(); await user.click(screen.getByRole('button', { name: 'Sign in' }))
    expect(await screen.findByRole('heading', { name: 'Welcome back, Alex.' })).toBeVisible()
  })
  it('provides a keyboard skip link and unknown-route recovery', async () => {
    setup('/missing')
    expect(screen.getByRole('heading', { name: 'That page is not here.' })).toBeVisible()
    await userEvent.tab()
    expect(screen.getByRole('link', { name: 'Skip to content' })).toHaveFocus()
    expect(screen.getByRole('link', { name: 'Back to TaskFlow' })).toHaveAttribute('href', '/')
  })
})
