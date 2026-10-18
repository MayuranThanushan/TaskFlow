import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'

function setup(authenticated = true) {
  const auth = server(authenticated)
  const state = { user: { ...profile }, status: 200, fields: {} as Record<string, string[]>, wait: null as Promise<void> | null, posts: [] as object[] }
  const client = createApiClient(async config => {
    if (config.url === '/auth/me') return reply(config, auth.state.authenticated ? 200 : 401, { ...state.user })
    if (config.url !== '/users/me') return auth.adapter(config)
    const body = JSON.parse(config.data as string) as { displayName: string }; state.posts.push(body)
    const saved = { ...state.user, displayName: body.displayName }; await state.wait
    if (state.status !== 200) return reply(config, state.status, { errors: state.fields })
    state.user = saved; return reply(config, 200, saved)
  })
  render(<MemoryRouter initialEntries={['/profile']}><App client={client} /></MemoryRouter>)
  return { state, auth, user: userEvent.setup() }
}

describe('profile', () => {
  it('protects the route before showing personal data', async () => {
    const { state } = setup(false)
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible()
    expect(screen.queryByLabelText('Display name')).not.toBeInTheDocument(); expect(state.posts).toHaveLength(0)
  })
  it('saves only a trimmed name, updates navigation, and retains read-only account data', async () => {
    const { state, user } = setup()
    const input = await screen.findByLabelText('Display name')
    expect(input).toHaveValue('Alex'); expect(screen.getByText('Email address: ' + profile.email)).toBeVisible()
    expect(screen.getAllByRole('textbox')).toHaveLength(1)
    await user.clear(input); await user.type(input, '  <Alex & Team>  '); await user.click(screen.getByRole('button', { name: 'Save profile' }))
    expect(await screen.findByText('Profile saved.')).toBeVisible(); expect(input).toHaveValue('<Alex & Team>')
    expect(screen.getByText('<Alex & Team>')).toBeVisible(); expect(state.posts).toEqual([{ displayName: '<Alex & Team>' }])
    expect(screen.getByText('Account ID: ' + profile.id)).toBeVisible()
  })
  it('validates blank and long names without writes and accepts the boundary', async () => {
    const { state, user } = setup(); const input = await screen.findByLabelText('Display name')
    await user.clear(input); await user.type(input, '   '); await user.click(screen.getByRole('button', { name: 'Save profile' }))
    expect(input).toHaveFocus(); expect(input).toHaveAttribute('aria-invalid', 'true'); expect(state.posts).toHaveLength(0)
    fireEvent.change(input, { target: { value: 'x'.repeat(101) } }); await user.click(screen.getByRole('button', { name: 'Save profile' })); expect(state.posts).toHaveLength(0)
    fireEvent.change(input, { target: { value: 'x'.repeat(100) } }); await user.click(screen.getByRole('button', { name: 'Save profile' })); expect(await screen.findByText('Profile saved.')).toBeVisible()
  })
  it('disables duplicate submissions and retains input after a safe failure for retry', async () => {
    const { state, user } = setup(); const input = await screen.findByLabelText('Display name')
    await user.clear(input); await user.type(input, 'New name')
    let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve }); state.status = 500
    await user.dblClick(screen.getByRole('button', { name: 'Save profile' })); expect(state.posts).toHaveLength(1); expect(input).toBeDisabled()
    await act(async () => { release() }); expect(await screen.findByRole('alert')).toHaveTextContent('TaskFlow could not complete')
    expect(input).toHaveValue('New name'); state.wait = null; state.status = 200
    await user.click(screen.getByRole('button', { name: 'Save profile' })); expect(await screen.findByText('Profile saved.')).toBeVisible()
  })
  it('shows field validation and a profile-specific concurrency conflict', async () => {
    const { state, user } = setup(); await screen.findByLabelText('Display name'); state.status = 400; state.fields = { displayName: ['Choose a valid name.'] }
    await user.click(screen.getByRole('button', { name: 'Save profile' })); expect(await screen.findByText('Choose a valid name.')).toBeVisible()
    state.status = 409; await user.click(screen.getByRole('button', { name: 'Save profile' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Your profile changed while saving.')
  })
  it('expires the session on a rejected save and removes private form data', async () => {
    const { state, user } = setup(); const input = await screen.findByLabelText('Display name')
    await user.clear(input); await user.type(input, 'Private draft'); state.status = 401
    await user.click(screen.getByRole('button', { name: 'Save profile' }))
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible()
    expect(screen.queryByLabelText('Display name')).not.toBeInTheDocument(); expect(screen.queryByDisplayValue('Private draft')).not.toBeInTheDocument()
  })
  it('ignores a delayed save from the previous account and clears its form', async () => {
    const { state, user } = setup(); const input = await screen.findByLabelText('Display name')
    await user.clear(input); await user.type(input, 'Private old name')
    let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve }); await user.click(screen.getByRole('button', { name: 'Save profile' }))
    state.user = { ...profile, id: 'other-user', displayName: 'Other', email: 'other@example.com' }; fireEvent(window, new Event('focus'))
    await waitFor(() => expect(screen.getByLabelText('Display name')).toHaveValue('Other'))
    await act(async () => { release() }); expect(screen.getByLabelText('Display name')).toHaveValue('Other')
    expect(screen.queryByText('Profile saved.')).not.toBeInTheDocument(); expect(screen.queryByText('Private old name')).not.toBeInTheDocument()
  })
})
