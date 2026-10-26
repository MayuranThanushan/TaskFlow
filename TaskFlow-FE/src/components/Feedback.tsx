import { useEffect, useRef } from 'react'
import type { ReactNode } from 'react'

export function RetryError({ message, retry, label = 'Try again' }: { message: string; retry: () => void; label?: string }) {
  return <div className="error-summary" role="alert"><p>{message}</p><button type="button" className="button subtle" onClick={retry}>{label}</button></div>
}

export function FieldError({ id, errors }: { id: string; errors?: string[] }) {
  return errors?.length ? <p className="field-error" id={id}>{errors.join(' ')}</p> : null
}

export function Pagination({ label, page, totalPages, onPage }: { label: string; page: number; totalPages: number; onPage: (page: number) => void }) {
  return totalPages > 1 ? <nav className="pagination" aria-label={label}>
    <button type="button" className="button subtle" disabled={page <= 1} onClick={() => onPage(page - 1)}>Previous</button>
    <span>Page {page} of {totalPages}</span>
    <button type="button" className="button subtle" disabled={page >= totalPages} onClick={() => onPage(page + 1)}>Next</button>
  </nav> : null
}

export function TableRegion({ label, children }: { label: string; children: ReactNode }) {
  return <div className="task-table-wrap" role="region" aria-label={label} tabIndex={0}>{children}</div>
}

export function Confirmation({ id, title, children, pending, cancelLabel, confirmLabel, pendingLabel, onCancel, onConfirm, headingLevel = 2 }: {
  id: string; title: ReactNode; children: ReactNode; pending: boolean
  cancelLabel: string; confirmLabel: string; pendingLabel: string
  onCancel: () => void; onConfirm: () => void; headingLevel?: 2 | 3
}) {
  const cancel = useRef<HTMLButtonElement>(null)
  useEffect(() => { cancel.current?.focus() }, [])
  const Heading = headingLevel === 3 ? 'h3' : 'h2'
  return <section className="delete-confirmation" aria-labelledby={id} onKeyDown={event => {
    if (event.key === 'Escape' && !pending) { event.stopPropagation(); onCancel() }
  }}>
    <Heading id={id}>{title}</Heading>{children}
    <div className="form-actions">
      <button ref={cancel} type="button" className="button subtle" disabled={pending} onClick={onCancel}>{cancelLabel}</button>
      <button type="button" className="button danger" disabled={pending} onClick={onConfirm}>{pending ? pendingLabel : confirmLabel}</button>
    </div>
  </section>
}
