import { act, fireEvent, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { StrictMode } from 'react'
import { MemoryRouter, useLocation, useNavigate } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import type { InternalAxiosRequestConfig } from 'axios'
import App from '../../App'
import { createApiClient } from '../../api/client'
import { profile, reply, server } from '../../test/server'
import type { ProjectDetails } from './types'

const project = (name = 'Launch', id = '9007199254741111'): ProjectDetails => ({ id, name, description: 'A useful project', ownerId: profile.id,
  createdAtUtc: '2026-10-18T08:00:00Z', updatedAtUtc: '2026-10-18T08:00:00Z', totalTasks: 8, completedTasks: 1, progressPercentage: 13 })
function Location() { const location = useLocation(); const navigate = useNavigate(); return <><span data-testid="location">{location.pathname + location.search}</span><button onClick={() => void navigate(-1)}>Browser back</button></> }
function setup(path = '/projects', projects: ProjectDetails[] = [], authenticated = true) {
  const auth = server(authenticated)
  const state = { projects, listStatus: 200, createStatus: 201, writeStatus: 200, wait: null as Promise<void> | null,
    currentProfile: { ...profile }, posts: [] as object[], calls: [] as InternalAxiosRequestConfig[] }
  const client = createApiClient(async config => {
    if (config.url === '/auth/me') return reply(config, auth.state.authenticated ? 200 : 401, state.currentProfile)
    if (!config.url?.startsWith('/projects')) return auth.adapter(config)
    state.calls.push(config)
    const url = new URL(config.url, 'https://taskflow.test')
    if (config.method === 'post') {
      const values = JSON.parse(config.data); state.posts.push(values); await state.wait
      if (state.createStatus !== 201) return reply(config, state.createStatus, { errors: { name: ['Please choose a valid name.'] } })
      const created = { ...project(), ...values, totalTasks: 0, completedTasks: 0, progressPercentage: 0 }; state.projects.push(created)
      return reply(config, 201, created)
    }
    if (config.method === 'put' || config.method === 'delete') {
      await state.wait
      if (state.writeStatus !== 200) return reply(config, state.writeStatus)
      const found = state.projects.find(item => '/projects/' + item.id === url.pathname)
      if (!found) return reply(config, 404)
      if (found.ownerId !== state.currentProfile.id) return reply(config, 403)
      if (config.method === 'delete') { state.projects = state.projects.filter(item => item !== found); return reply(config, 204) }
      Object.assign(found, JSON.parse(config.data)); return reply(config, 200, found)
    }
    if (url.pathname === '/projects') {
      if (state.listStatus !== 200) return reply(config, state.listStatus)
      let items = state.projects.filter(item => item.name.toLowerCase().includes((url.searchParams.get('search') ?? '').toLowerCase()))
      if (url.searchParams.get('sortBy') === 'name') items = [...items].sort((a, b) => a.name.localeCompare(b.name))
      const page = Number(url.searchParams.get('page') ?? 1); const pageSize = Number(url.searchParams.get('pageSize') ?? 20)
      return reply(config, 200, { items: items.slice((page - 1) * pageSize, page * pageSize), page, pageSize, totalCount: items.length, totalPages: Math.ceil(items.length / pageSize) })
    }
    const found = state.projects.find(item => '/projects/' + item.id === url.pathname)
    return reply(config, found ? 200 : 404, found)
  })
  render(<StrictMode><MemoryRouter initialEntries={[path]}><App client={client} /><Location /></MemoryRouter></StrictMode>)
  return state
}

describe('Project pages', () => {
  it('prefills owner editing and replaces the description with null when cleared', async () => {
    const state = setup('/projects/9007199254741111', [project()]); const user = userEvent.setup()
    await user.click(await screen.findByRole('link', { name: 'Edit project' }))
    expect(await screen.findByLabelText('Project name')).toHaveValue('Launch')
    await user.clear(screen.getByLabelText('Project name')); await user.type(screen.getByLabelText('Project name'), ' Revised ')
    await user.clear(screen.getByLabelText(/Description/)); await user.click(screen.getByRole('button', { name: 'Save changes' }))
    expect(await screen.findByRole('heading', { name: 'Revised' })).toBeVisible()
    expect(screen.getByText('Project updated.')).toBeVisible()
    expect(JSON.parse(state.calls.find(call => call.method === 'put')!.data)).toEqual({ name: 'Revised', description: null })
  })
  it('hides owner actions and prevents a member from opening the edit form', async () => {
    setup('/projects/9007199254741111/edit', [{ ...project(), ownerId: 'other' }])
    expect(await screen.findByRole('heading', { name: 'Only the owner can edit this project.' })).toBeVisible()
    expect(screen.queryByLabelText('Project name')).not.toBeInTheDocument()
  })
  it('requires confirmation, supports cancellation and deletes once while pending', async () => {
    const state = setup('/projects/9007199254741111', [project()]); const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Delete project' }))
    expect(screen.getByRole('button', { name: 'Keep project' })).toHaveFocus()
    await user.keyboard('{Escape}')
    expect(state.calls.filter(call => call.method === 'delete')).toHaveLength(0)
    await user.click(screen.getByRole('button', { name: 'Delete project' }))
    let release!: () => void; state.wait = new Promise<void>(resolve => { release = resolve })
    await user.dblClick(screen.getByRole('button', { name: 'Confirm deletion' }))
    expect(screen.getByRole('button', { name: 'Deleting project...' })).toBeDisabled()
    expect(state.calls.filter(call => call.method === 'delete')).toHaveLength(1)
    await act(async () => { release() })
    expect(await screen.findByText('Project deleted.')).toBeVisible()
    expect(await screen.findByRole('heading', { name: 'Your first project starts here.' })).toBeVisible()
  })
  it('keeps a project available when deletion fails and permits an explicit retry', async () => {
    const state = setup('/projects/9007199254741111', [project()]); state.writeStatus = 500; const user = userEvent.setup()
    await user.click(await screen.findByRole('button', { name: 'Delete project' }))
    await user.click(screen.getByRole('button', { name: 'Confirm deletion' }))
    expect(await screen.findByRole('alert')).toBeVisible(); expect(state.projects).toHaveLength(1)
    state.writeStatus = 200; await user.click(screen.getByRole('button', { name: 'Confirm deletion' }))
    expect(await screen.findByText('Project deleted.')).toBeVisible()
  })
  it('protects creation before any project request', async () => {
    const state = setup('/projects/new', [], false)
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible()
    expect(state.calls).toHaveLength(0)
  })
  it('shows the empty state and creates a project with an opaque ID and no owner payload', async () => {
    const state = setup(); const user = userEvent.setup()
    expect(await screen.findByRole('heading', { name: 'Your first project starts here.' })).toBeVisible()
    await user.click(screen.getByRole('link', { name: 'New project' }))
    await user.type(screen.getByLabelText('Project name'), ' Launch ')
    await user.type(screen.getByLabelText(/Description/), ' Context ')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    expect(await screen.findByRole('heading', { name: 'Launch' })).toBeVisible()
    expect(screen.getByTestId('location')).toHaveTextContent('/projects/9007199254741111')
    expect(screen.getByText('Project created.')).toBeVisible()
    expect(state.posts).toEqual([{ name: 'Launch', description: 'Context' }])
    expect(screen.getByRole('progressbar', { name: 'Project progress' })).toHaveAttribute('value', '0')
    await user.click(screen.getByRole('link', { name: 'Back to projects' }))
    expect(await screen.findByRole('link', { name: 'Launch' })).toBeVisible()
  })
  it('validates a blank name without posting and focuses its associated field', async () => {
    const state = setup('/projects/new')
    await screen.findByLabelText('Project name')
    await userEvent.click(screen.getByRole('button', { name: 'Create project' }))
    expect(screen.getByLabelText('Project name')).toHaveFocus()
    expect(screen.getByLabelText('Project name')).toHaveAttribute('aria-invalid', 'true')
    expect(state.posts).toHaveLength(0)
  })
  it('prevents duplicate creates while pending and sends blank descriptions as null', async () => {
    const state = setup('/projects/new'); let release!: () => void
    state.wait = new Promise<void>(resolve => { release = resolve })
    const user = userEvent.setup(); await user.type(await screen.findByLabelText('Project name'), 'Launch')
    await user.dblClick(screen.getByRole('button', { name: 'Create project' }))
    expect(screen.getByLabelText('Project name')).toBeDisabled()
    expect(state.posts).toEqual([{ name: 'Launch', description: null }])
    await act(async () => { release() })
    expect(await screen.findByRole('heading', { name: 'Launch' })).toBeVisible()
  })
  it('retains input, associates server field errors and focuses the error summary', async () => {
    const state = setup('/projects/new'); state.createStatus = 400
    const user = userEvent.setup(); await user.type(await screen.findByLabelText('Project name'), 'Launch')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    const alert = await screen.findByRole('alert'); await waitFor(() => expect(alert).toHaveFocus())
    expect(screen.getByLabelText('Project name')).toHaveValue('Launch')
    expect(screen.getByLabelText('Project name')).toHaveAccessibleDescription('Please choose a valid name.')
    expect(screen.getByRole('button', { name: 'Create project' })).toBeEnabled()
  })
  it('recovers a failed list request without losing navigation', async () => {
    const state = setup(); state.listStatus = 503
    expect(await screen.findByRole('alert')).toHaveTextContent('could not complete')
    state.listStatus = 200; state.projects.push(project())
    await userEvent.click(screen.getByRole('button', { name: 'Try again' }))
    expect(await screen.findByRole('link', { name: 'Launch' })).toBeVisible()
  })
  it('keeps search, sort and pagination in the URL and restores them on back navigation', async () => {
    setup('/projects', Array.from({ length: 22 }, (_, index) => project('Project ' + String(index).padStart(2, '0'), String(index + 1))))
    const user = userEvent.setup(); await screen.findByRole('link', { name: 'Project 00' })
    await user.click(screen.getByRole('button', { name: 'Next' }))
    expect(await screen.findByRole('link', { name: 'Project 21' })).toBeVisible()
    expect(screen.getByTestId('location')).toHaveTextContent('page=2')
    await user.type(screen.getByLabelText('Search projects'), 'Project 01')
    await user.selectOptions(screen.getByLabelText('Sort by'), 'name')
    await user.selectOptions(screen.getByLabelText('Order'), 'asc')
    await user.click(screen.getByRole('button', { name: 'Apply' }))
    expect(await screen.findByRole('link', { name: 'Project 01' })).toBeVisible()
    expect(screen.getByTestId('location').textContent).toContain('search=Project+01')
    expect(screen.getByTestId('location').textContent).not.toContain('page=2')
    await user.click(screen.getByRole('button', { name: 'Browser back' }))
    expect(await screen.findByRole('link', { name: 'Project 21' })).toBeVisible()
    expect(screen.getByLabelText('Search projects')).toHaveValue('')
  })
  it('shows a useful no-match state and renders descriptions as plain text with real progress', async () => {
    const item = { ...project(), description: '<img src=x onerror=alert(1)>', ownerId: 'another-user' }
    setup('/projects/' + item.id, [item])
    expect(await screen.findByText(item.description)).toBeVisible()
    expect(document.querySelector('.project-description img')).toBeNull()
    expect(screen.getByText('Shared project')).toBeVisible()
    expect(screen.getByRole('progressbar', { name: 'Project progress' })).toHaveAttribute('value', '13')
  })
  it('keeps inaccessible resources generic and leaves the session signed in', async () => {
    setup('/projects/123')
    expect(await screen.findByRole('heading', { name: 'Project not available.' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeVisible()
    expect(screen.getByRole('link', { name: 'Back to projects' })).toBeVisible()
  })
  it('redirects a project API 401 to sign-in with the original destination', async () => {
    const state = setup('/projects?search=private'); state.listStatus = 401
    expect(await screen.findByRole('heading', { name: 'Sign in to TaskFlow' })).toBeVisible()
    expect(screen.getByText('Your session has expired. Sign in again.')).toBeVisible()
  })
  it('clears the previous account data and refetches after a cross-tab account change', async () => {
    const state = setup('/projects', [project('Private old account')])
    await screen.findByRole('link', { name: 'Private old account' })
    state.currentProfile = { ...profile, id: 'user-2', displayName: 'Other user' }; state.projects = []
    await act(async () => { fireEvent.focus(window) })
    expect(await screen.findByRole('heading', { name: 'Your first project starts here.' })).toBeVisible()
    expect(screen.queryByRole('link', { name: 'Private old account' })).not.toBeInTheDocument()
    expect(state.calls.filter(call => call.url === '/projects').length).toBeGreaterThanOrEqual(2)
  })
  it('clears a private draft and ignores a pending create completion after an account switch', async () => {
    const state = setup('/projects/new'); let release!: () => void
    state.wait = new Promise<void>(resolve => { release = resolve })
    const user = userEvent.setup(); await user.type(await screen.findByLabelText('Project name'), 'Private draft')
    await user.click(screen.getByRole('button', { name: 'Create project' }))
    expect(screen.getByLabelText('Project name')).toBeDisabled()
    state.currentProfile = { ...profile, id: 'user-2', displayName: 'Other user' }
    await act(async () => { fireEvent.focus(window) })
    await waitFor(() => expect(screen.getByLabelText('Project name')).toHaveValue(''))
    await act(async () => { release() })
    expect(screen.getByTestId('location')).toHaveTextContent('/projects/new')
    expect(screen.getByLabelText('Project name')).toHaveValue('')
  })
  it('clears the old detail and document title when a changed account has no access', async () => {
    const item = project('Private title'); const state = setup('/projects/' + item.id, [item])
    await screen.findByRole('heading', { name: 'Private title' })
    expect(document.title).toBe('Private title | TaskFlow')
    state.currentProfile = { ...profile, id: 'user-2', displayName: 'Other user' }; state.projects = []
    await act(async () => { fireEvent.focus(window) })
    expect(await screen.findByRole('heading', { name: 'Project not available.' })).toBeVisible()
    expect(screen.queryByRole('heading', { name: 'Private title' })).not.toBeInTheDocument()
    expect(document.title).toBe('Project | TaskFlow')
  })
  it('offers recovery from an out-of-range page and from an empty search', async () => {
    setup('/projects?page=50', [project()])
    expect(await screen.findByRole('heading', { name: 'No projects on this page.' })).toBeVisible()
    await userEvent.click(screen.getByRole('button', { name: 'Go to first page' }))
    expect(await screen.findByRole('link', { name: 'Launch' })).toBeVisible()
    await userEvent.type(screen.getByLabelText('Search projects'), 'No match')
    await userEvent.click(screen.getByRole('button', { name: 'Apply' }))
    expect(await screen.findByRole('heading', { name: 'No projects match your search.' })).toBeVisible()
    await userEvent.click(screen.getByRole('link', { name: 'Reset' }))
    expect(await screen.findByRole('link', { name: 'Launch' })).toBeVisible()
  })
})
