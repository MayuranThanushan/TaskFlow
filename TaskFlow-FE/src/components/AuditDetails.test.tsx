import { render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { AuditDetails } from './AuditDetails'

describe('audit metadata', () => {
  it('labels legacy history without inventing a successful outcome or deltas', () => {
    render(<AuditDetails audit={{ schemaVersion: 0, isLegacy: true, outcome: 'LegacyUnknown', correlationId: null, subjectType: null, subjectId: null, changes: [] }} />)
    expect(screen.getByText('Legacy record')).toBeInTheDocument()
    expect(screen.queryByText(/Succeeded/)).not.toBeInTheDocument()
  })
  it('withholds redacted values and shows safe IDs/enums with correlation', () => {
    render(<AuditDetails audit={{ schemaVersion: 1, isLegacy: false, outcome: 'Succeeded', correlationId: 'abc123', subjectType: 'Task', subjectId: '9007199254741999', changes: [{ field: 'title', before: 'Private canary', after: 'Other private canary', redacted: true }, { field: 'status', before: 'ToDo', after: 'Done', redacted: false }] }} />)
    expect(screen.queryByText(/Private canary/i)).not.toBeInTheDocument()
    expect(screen.getByText(/values withheld/)).toBeInTheDocument()
    expect(screen.getByText(/ToDo → Done/)).toBeInTheDocument()
    expect(screen.getByText(/9007199254741999/)).toBeInTheDocument()
  })
})
