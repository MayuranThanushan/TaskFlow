import { describe, expect, it, vi } from 'vitest'
import { createApiClient } from './client'
import { reply } from '../test/server'

describe('company request boundaries', () => {
  it('captures a company per client while sharing the root session and CSRF token', async () => {
    const calls: string[] = []
    let tokens = 0
    const client = createApiClient(async c => {
      if (c.url === '/auth/csrf') { tokens++; expect(c.baseURL).toBe('/api/v1'); return reply(c, 200, { requestToken: 'paired' }) }
      calls.push(c.baseURL + c.url!)
      expect(c.headers.get('X-CSRF-TOKEN')).toBe('paired')
      return reply(c, 201, {})
    })
    const a = client.forWorkspace('1'), b = client.forWorkspace('2')
    await Promise.all([a.request({ url: '/projects', method: 'post' }), b.request({ url: '/projects', method: 'post' })])
    expect(calls.sort()).toEqual(['/api/v2/workspaces/1/projects', '/api/v2/workspaces/2/projects'])
    expect(tokens).toBe(1)
    expect(a.resourceKey).not.toBe(b.resourceKey)
  })
  it('cancels a queued write before transport when switching during CSRF acquisition', async () => {
    let release!: () => void
    const wait = new Promise<void>(resolve => { release = resolve })
    const sent: string[] = []
    const client = createApiClient(async c => {
      if (c.url === '/auth/csrf') { await wait; return reply(c, 200, { requestToken: 'paired' }) }
      sent.push(c.baseURL!); return reply(c, 200)
    })
    const old = client.forWorkspace('1')
    const queued = old.request({ url: '/projects', method: 'post' })
    const rejected = expect(queued).rejects.toThrow()
    old.cancelRequests(); const next = client.forWorkspace('2'); release()
    await rejected
    await next.request({ url: '/projects', method: 'post' })
    expect(sent).toEqual(['/api/v2/workspaces/2'])
  })
  it('rejects identity changes while a company write waits for CSRF', async () => {
    let release!: () => void
    const wait = new Promise<void>(resolve => { release = resolve })
    let writes = 0
    const client = createApiClient(async c => {
      if (c.url === '/auth/csrf') { await wait; return reply(c, 200, { requestToken: 'paired' }) }
      writes++; return reply(c, 200)
    })
    const queued = client.forWorkspace('1').request({ url: '/projects', method: 'post' })
    const rejected = expect(queued).rejects.toThrow('Your account changed')
    client.forgetSession(); release(); await rejected; expect(writes).toBe(0)
  })
  it('uses root expiry listeners and rejects non-resource paths or invalid IDs', async () => {
    const client = createApiClient(async c => reply(c, 401))
    const expire = vi.fn(); client.onUnauthorized(expire)
    await expect(client.forWorkspace('1').request({ url: '/projects' })).rejects.toMatchObject({ status: 401 })
    expect(expire).toHaveBeenCalledOnce()
    for (const id of ['0', '-1', '01', '../1', '9223372036854775808']) expect(() => client.forWorkspace(id)).toThrow()
    for (const url of ['/auth/logout', '//evil.test', '/projectsOther']) await expect(client.forWorkspace('1').request({ url })).rejects.toThrow()
  })
})
