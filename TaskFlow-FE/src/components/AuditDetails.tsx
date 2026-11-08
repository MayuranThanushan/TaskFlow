import type { components } from '../api/generated'

export function AuditDetails({ audit }: { audit?: components['schemas']['AuditMetadataDto'] }) {
  if (!audit) return null
  return <details className="audit-metadata"><summary>{audit.isLegacy ? 'Legacy record' : 'Audit v' + audit.schemaVersion + ' · ' + audit.outcome}</summary>
    {audit.isLegacy ? <p>Historical changes and outcome were not recorded in this format.</p> : audit.outcome === 'UnsupportedVersion' ? <p>This audit format requires a newer reader. Field values are withheld.</p> : <>
      <p>Subject: {audit.subjectType} {audit.subjectId}</p>
      <p>Correlation ID: <code>{audit.correlationId}</code></p>
      {audit.changes.length > 0 ? <ul>{audit.changes.map(change => <li key={change.field}><strong>{change.field}</strong>: {change.redacted ? 'changed (values withheld)' : (change.before ?? 'None') + ' → ' + (change.after ?? 'None')}</li>)}</ul> : <p>No field changes recorded for this event.</p>}
    </>}
  </details>
}
