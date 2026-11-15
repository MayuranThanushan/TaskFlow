import { describe, expect, it } from 'vitest'
import { createApiClient } from '../../api/client'
import { reply } from '../../test/server'
import { readPlacement } from './board-state'

function fixture(count: number, drift = false) {
  const calls: number[] = []
  const client = createApiClient(async c => {
    const page = c.params.page as number; calls.push(page)
    const total = drift && page > 1 ? count + 1 : count
    return reply(c, 200, { page, pageSize: 100, totalCount: total, totalPages: Math.ceil(total / 100), items: Array.from({ length: Math.min(100, Math.max(0, count - (page - 1) * 100)) }, (_, i) => ({ id: String((page - 1) * 100 + i + 1), status: 'ToDo', version: '1', title: 'Task' })) })
  }).forWorkspace('1')
  return { client, calls }
}
describe('canonical board placement preparation', () => {
  it('reads every required page and retains the off-page neighbors for 205 tasks', async () => {
    const { client, calls } = fixture(205)
    const result = await readPlacement(client, '41', 'ToDo', '101', new AbortController().signal)
    expect(calls).toEqual([1, 2, 3]); expect(result.items).toHaveLength(204); expect(result.sourceIndex).toBe(100)
    expect(result.items[99].id).toBe('100'); expect(result.items[100].id).toBe('102')
  })
  it('rejects a drifting multi-page column instead of inventing a position', async () => {
    const { client } = fixture(205, true)
    await expect(readPlacement(client, '41', 'ToDo', '1', new AbortController().signal)).rejects.toThrow('column changed')
  })
  it('refuses an oversized column before unbounded fetches', async () => {
    const { client, calls } = fixture(501)
    await expect(readPlacement(client, '41', 'ToDo', '1', new AbortController().signal)).rejects.toThrow('ordering limit')
    expect(calls).toEqual([1])
  })
})
