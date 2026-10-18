import { describe, expect, it, vi } from 'vitest'
import { AxiosError } from 'axios'
import { createApiClient } from './client'
import { reply, server } from '../test/server'
import { returnPath } from '../features/auth/navigation'

describe('API client', () => {
  it('shares CSRF acquisition and attaches cookies and tokens to writes', async () => {
    const { state, adapter } = server()
    const client = createApiClient(adapter)
    await Promise.all([client.register({ displayName: 'Alex', email: 'a@b.com', password: 'Password123!' }),
      client.register({ displayName: 'Alex', email: 'b@b.com', password: 'Password123!' })])
    expect(state.tokens).toBe(1)
    for (const call of state.calls.filter(call => call.method === 'post')) {
      expect(call.headers.get('X-CSRF-TOKEN')).toBe('token-1')
      expect(call.withCredentials).toBe(true)
      expect(call.baseURL).toBe('/api/v1')
    }
  })
  it('refreshes a rejected CSRF token once', async () => {
    const { state, adapter } = server(); state.csrfFailures = 1
    await createApiClient(adapter).login({ email: 'a@b.com', password: 'p' })
    expect(state.calls.filter(call => call.url === '/auth/login')).toHaveLength(2)
    expect(state.calls.filter(call => call.url === '/auth/login')[1].headers.get('X-CSRF-TOKEN')).toBe('token-2')
  })
  it('does not loop when refreshed CSRF is also rejected', async () => {
    const { state, adapter } = server(); state.csrfFailures = 3
    await expect(createApiClient(adapter).login({ email: 'a@b.com', password: 'p' })).rejects.toMatchObject({ status: 400 })
    expect(state.calls.filter(call => call.method === 'post')).toHaveLength(2)
  })
  it('never retries network failures or server errors on writes', async () => {
    for (const status of [0, 500]) {
      let writes = 0
      const client = createApiClient(async config => {
        if (config.url === '/auth/csrf') return reply(config, 200, { requestToken: 'token' })
        writes++
        if (!status) throw new AxiosError('Network Error')
        return reply(config, status)
      })
      await expect(client.login({ email: 'a@b.com', password: 'p' })).rejects.toThrow()
      expect(writes).toBe(1)
    }
  })
  it('distinguishes login rejection from an expired protected request', async () => {
    const { state, adapter } = server(); state.loginStatus = 401
    const client = createApiClient(adapter); const expired = vi.fn(); client.onUnauthorized(expired)
    await expect(client.login({ email: 'a@b.com', password: 'p' })).rejects.toMatchObject({ status: 401 })
    expect(expired).not.toHaveBeenCalled()
    await expect(client.request({ url: '/projects' })).rejects.toMatchObject({ status: 401 })
    expect(expired).toHaveBeenCalledOnce()
  })
  it('rejects external paths and forces the API base URL', async () => {
    const { state, adapter } = server(); const client = createApiClient(adapter)
    for (const url of ['https://evil.test/', '//evil.test/', '/\\evil.test/'])
      await expect(client.request({ url })).rejects.toThrow('Invalid API request')
    await expect(client.request({ url: '/projects', baseURL: 'https://evil.test' })).rejects.toThrow()
    expect(state.calls[0].baseURL).toBe('/api/v1')
  })
  it('ignores an unauthorized response from before a successful sign-in', async () => {
    let rejectOld!: () => void
    const mock = server()
    const client = createApiClient(async config => {
      if (config.url === '/slow') return new Promise((_resolve, reject) => {
        rejectOld = () => { try { reply(config, 401) } catch (error) { reject(error) } }
      })
      return mock.adapter(config)
    })
    const expired = vi.fn(); client.onUnauthorized(expired)
    const oldRequest = client.request({ url: '/slow' }).catch(error => error)
    await client.login({ email: 'a@b.com', password: 'p' })
    rejectOld(); await oldRequest
    expect(expired).not.toHaveBeenCalled()
  })
  it('acquires fresh identity-bound tokens after login and logout', async () => {
    const { state, adapter } = server(); const client = createApiClient(adapter)
    await client.login({ email: 'a@b.com', password: 'p' })
    await client.logout()
    const logout = state.calls.find(call => call.url === '/auth/logout')!
    expect(logout.headers.get('X-CSRF-TOKEN')).toBe('token-2')
    await client.register({ displayName: 'Alex', email: 'a@b.com', password: 'p' })
    expect(state.calls.find(call => call.url === '/auth/register')!.headers.get('X-CSRF-TOKEN')).toBe('token-3')
  })
  it('accepts only safe internal return destinations', () => {
    for (const path of ['//evil.test', 'https://evil.test', '/login', '/register', '/\\evil.test']) expect(returnPath(path)).toBe('/dashboard')
    expect(returnPath('/dashboard?view=mine#tasks')).toBe('/dashboard?view=mine#tasks')
  })
})
