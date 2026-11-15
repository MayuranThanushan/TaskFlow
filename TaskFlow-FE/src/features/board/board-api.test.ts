import { describe, expect, it } from 'vitest'
import { createApiClient } from '../../api/client'
import { reply } from '../../test/server'
import { getBoard, getBoardColumn } from './board-api'
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
})
