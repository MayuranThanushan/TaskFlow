import { describe, expect, it } from 'vitest'
import { createApiClient } from '../../api/client'
import { reply } from '../../test/server'
import { getBoard, getBoardColumn, moveBoardTask } from './board-api'
import type { BoardStatus } from './board-api'

describe('board read client', () => {
  it('binds counts and independently paged columns to the captured company and filters', async () => {
    const calls: { url: string, baseURL?: string, params: unknown }[] = []
    const client = createApiClient(async c => {
      calls.push({ url: c.url!, baseURL: c.baseURL, params: c.params })
      expect(c.method).toBe('get')
      expect(c.withCredentials).toBe(true)
      expect(c.headers.has('X-CSRF-TOKEN')).toBe(false)
      return reply(c, 200, c.url!.endsWith('/board') ? { projectId: '3', totalCount: 125, columns: [{ status: 'ToDo', totalCount: 125 }, { status: 'InProgress', totalCount: 0 }, { status: 'Done', totalCount: 0 }] } : { items: [], page: 2, pageSize: 100, totalCount: 125, totalPages: 2 })
    })
    const company = client.forWorkspace('9')
    const counts = await getBoard(company, '3', { search: 'Review', priority: 'High', assigneeId: '5' })
    const page = await getBoardColumn(company, '3', 'ToDo', { page: 2, pageSize: 100, search: 'Review', priority: 'High', assigneeId: '5' })
    expect(counts.totalCount).toBe(125)
    expect(page.page).toBe(2)
    expect(calls).toEqual([
      { url: '/projects/3/board', baseURL: '/api/v2/workspaces/9', params: { search: 'Review', priority: 'High', assigneeId: '5' } },
      { url: '/projects/3/board/columns/ToDo', baseURL: '/api/v2/workspaces/9', params: { page: 2, pageSize: 100, search: 'Review', priority: 'High', assigneeId: '5' } },
    ])
  })
  it('rejects legacy scope, unsafe IDs and unknown columns before transport', () => {
    let sent = 0
    const root = createApiClient(async c => { sent++; return reply(c, 200, {}) })
    expect(() => getBoard(root, '3')).toThrow('Select a company')
    for (const id of ['0', '../4', '9223372036854775808', 'https://example.test']) expect(() => getBoard(root.forWorkspace('1'), id)).toThrow('Invalid project')
    expect(() => getBoardColumn(root.forWorkspace('1'), '3', 'Other' as BoardStatus)).toThrow('Invalid board column')
    expect(sent).toBe(0)
  })
  it('honors cancellation and propagates access revocation rather than returning an empty board', async () => {
    let sent = 0
    const root = createApiClient(async c => { sent++; return reply(c, 404, { title: 'Not found' }) })
    const company = root.forWorkspace('1'), stop = new AbortController()
    stop.abort()
    await expect(getBoardColumn(company, '3', 'Done', {}, stop.signal)).rejects.toThrow()
    expect(sent).toBe(0)
    await expect(getBoard(company, '3')).rejects.toMatchObject({ status: 404 })
    expect(sent).toBe(1)
  })
  it('posts only captured position/revisions with paired CSRF and current company', async () => {
    const writes: string[] = []
    const draft = { status: 'Done' as const, previousTaskId: '7', previousVersion: '4', nextTaskId: '8', nextVersion: '2' }
    const company = createApiClient(async c => {
      if (c.url === '/auth/csrf') return reply(c, 200, { requestToken: 'paired' })
      writes.push(c.url!)
      expect(c.baseURL).toBe('/api/v2/workspaces/1')
      expect(c.headers.get('X-Expected-Version')).toBe('3')
      expect(c.headers.get('X-CSRF-TOKEN')).toBe('paired')
      expect(JSON.parse(c.data)).toEqual(draft)
      return reply(c, 200, { id: '5', status: 'Done', version: '4' })
    }).forWorkspace('1')
    const result = await moveBoardTask(company, '2', '5', '3', draft)
    expect(result.version).toBe('4')
    expect(writes).toEqual(['/projects/2/tasks/5/move'])
  })
  it('propagates a move conflict without fetching revisions or silently retrying', async () => {
    const calls: string[] = []
    const company = createApiClient(async c => {
      calls.push(c.url!)
      return c.url === '/auth/csrf' ? reply(c, 200, { requestToken: 'paired' }) : reply(c, 409, { code: 'board.position_conflict' })
    }).forWorkspace('1')
    await expect(moveBoardTask(company, '2', '5', '3', { status: 'Done' })).rejects.toMatchObject({ status: 409, code: 'board.position_conflict' })
    expect(calls).toEqual(['/auth/csrf', '/projects/2/tasks/5/move'])
  })
  it('refuses invalid source revisions/IDs and cancels a pending move before dispatch', async () => {
    let writes = 0
    const root = createApiClient(async c => { writes++; return reply(c, 200, {}) }), company = root.forWorkspace('1')
    for (const version of ['', '01', '0', '9223372036854775808']) expect(() => moveBoardTask(company, '2', '5', version, { status: 'Done' })).toThrow('Invalid revision')
    expect(() => moveBoardTask(company, '2', '../5', '1', { status: 'Done' })).toThrow('Invalid task')
    expect(() => moveBoardTask(root, '2', '5', '1', { status: 'Done' })).toThrow('Select a company')
    const stop = new AbortController(); stop.abort()
    await expect(moveBoardTask(company, '2', '5', '1', { status: 'Done' }, stop.signal)).rejects.toThrow()
    expect(writes).toBe(0)
  })

})
