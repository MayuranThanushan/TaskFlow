import { useEffect, useRef, useState } from 'react'
import { asApiError, type ApiClient } from '../api/client'
import { TableRegion } from './Feedback'

export function ConflictReview<T extends { version: string }>({ client, path, draft, values, onKeep, onReload }: {
  client: ApiClient; path: string; draft: Record<string, string>; values: (record: T) => Record<string, string>;
  onKeep: (record: T) => void; onReload: (record: T) => void;
}) {
  const [latest, setLatest] = useState<T>(); const [pending, setPending] = useState(false); const [error, setError] = useState('')
  const alive = useRef(true), busy = useRef(false)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  async function compare() {
    if (busy.current) return
    busy.current = true; setPending(true); setError(''); setLatest(undefined)
    try { const record = await client.request<T>({ url: path }); if (alive.current) setLatest(record) }
    catch (failure) { if (alive.current) setError(asApiError(failure).message) }
    finally { busy.current = false; if (alive.current) setPending(false) }
  }
  const current = latest ? values(latest) : {}
  return <section className="conflict-review" aria-label="Resolve editing conflict">
    <h2>Review changes before saving</h2><p>Your draft is still in the form. Load the latest record to compare changes. Saving stays paused until you choose how to continue.</p>
    {error && <p role="alert">{error} Your draft has been kept.</p>}
    <button type="button" className="button subtle" disabled={pending} onClick={() => void compare()}>{pending ? 'Loading latest?' : 'Compare latest version'}</button>
    {latest && <><TableRegion label="Draft and latest comparison"><table className="task-table"><thead><tr><th scope="col">Field</th><th scope="col">Your draft</th><th scope="col">Latest saved</th></tr></thead><tbody>{Object.entries(draft).map(([key, value]) => <tr key={key}><th scope="row">{key}</th><td>{value || 'None'}</td><td>{current[key] || 'None'}</td></tr>)}</tbody></table></TableRegion>
      <p>Keeping your edits also updates untouched fields from the latest record. Review the form, then save explicitly. Another edit may cause a new conflict.</p>
      <div className="form-actions"><button type="button" className="button primary" onClick={() => onKeep(latest)}>Keep my edits</button><button type="button" className="button subtle" onClick={() => onReload(latest)}>Discard draft and load latest</button></div></>}
  </section>
}
