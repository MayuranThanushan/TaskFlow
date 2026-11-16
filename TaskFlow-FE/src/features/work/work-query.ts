import type { components } from '../../api/generated'
export type WorkPage = components['schemas']['WorkPage']
export type WorkItem = components['schemas']['WorkItemDto']
export const queues = [{ value: 'open', label: 'Open work', count: 'open' }, { value: 'due', label: 'Due soon', count: 'due' }, { value: 'overdue', label: 'Overdue', count: 'overdue' }, { value: 'all', label: 'All tasks', count: 'total' }] as const
export function dateBound(value: string, end = false): string {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(value)) return value
  const date = new Date(value + 'T00:00:00')
  const local = [date.getFullYear(), String(date.getMonth() + 1).padStart(2, '0'), String(date.getDate()).padStart(2, '0')].join('-')
  if (!Number.isFinite(date.getTime()) || local !== value) return value
  if (!end) return date.toISOString()
  // Next local midnight follows calendar/DST rules, rather than adding 24 hours.
  date.setDate(date.getDate() + 1)
  return new Date(date.getTime() - 1).toISOString().replace(/\.999Z$/, '.999999Z')
}
export function workPath(params: URLSearchParams) {
  const api = new URLSearchParams()
  for (const [key, value] of params) {
    if (key === 'fromDate') api.append('fromUtc', dateBound(value))
    else if (key === 'throughDate') api.append('toUtc', dateBound(value, true))
    else api.append(key, value)
  }
  return '/work' + (api.size ? '?' + api.toString() : '')
}
