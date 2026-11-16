import { describe, expect, it } from 'vitest'
import { dateBound, workPath } from './work-query'
describe('work query boundaries', () => {
  it('converts local calendar midnight to explicit UTC', () => { expect(dateBound('2026-11-16')).toBe(new Date('2026-11-16T00:00:00').toISOString()) })
  it('includes the last database microsecond of the local day', () => { const next = new Date('2026-11-17T00:00:00'); expect(dateBound('2026-11-16', true)).toBe(new Date(next.getTime() - 1).toISOString().replace('.999Z', '.999999Z')) })
  it('preserves invalid dates for server rejection', () => { for (const value of ['2026-02-30', 'invalid', '2026-13-01']) expect(dateBound(value)).toBe(value) })
  it('translates calendar filters without discarding strict validation inputs', () => { const q = new URL(workPath(new URLSearchParams('fromDate=2026-11-16&fromUtc=bad&queue=due&queue=all&unknown=x')), 'https://example.test'); expect(q.searchParams.getAll('fromUtc')).toEqual([dateBound('2026-11-16'), 'bad']); expect(q.searchParams.getAll('queue')).toEqual(['due', 'all']); expect(q.searchParams.get('unknown')).toBe('x') })
  it('keeps a bare default endpoint', () => { expect(workPath(new URLSearchParams())).toBe('/work') })
})
