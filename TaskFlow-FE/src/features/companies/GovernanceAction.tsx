import { useEffect, useRef, useState } from 'react'
import type { ReactNode } from 'react'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import { Confirmation } from '../../components/Feedback'

export function GovernanceAction({ client, path, method, title, children, data, passwordRequired = false, description, onSuccess }: {
  client: ApiClient; path: string; method: 'post' | 'patch' | 'put'; title: string; children?: ReactNode;
  data: Record<string, string>; passwordRequired?: boolean; description: string; onSuccess: () => void;
}) {
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState(false)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const lock = useRef(false), alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  async function save() {
    if (lock.current) return
    lock.current = true; setPending(true); setError(''); setNotice('')
    try {
      await client.request({ url: path, method, data: { ...data, ...(passwordRequired ? { password } : {}) } })
      if (!alive.current) return
      setPassword(''); setConfirm(false); setNotice('Change saved.'); onSuccess()
    } catch (failure) {
      if (!alive.current) return
      const e = asApiError(failure)
      setError(e.status === 409 ? 'This change conflicts with current membership or ownership. Transfer active owned projects first and keep an active company Owner.' : e.status === 403 ? 'Access denied or password verification failed. Check your permissions and re-enter your password.' : e.message)
      setPassword(''); setConfirm(false)
    } finally { lock.current = false; if (alive.current) setPending(false) }
  }
  return <div className="governance-action">
    <form aria-label={title} onSubmit={e => { e.preventDefault(); if (!pending) { setError(''); setNotice(''); setConfirm(true) } }}>
      <fieldset disabled={pending || confirm}><legend>{title}</legend>{children}
        {passwordRequired && <label>Current password<input type="password" autoComplete="current-password" required maxLength={128} value={password} onChange={e => setPassword(e.target.value)} /></label>}
        <button className="button subtle" type="submit">Review change</button>
      </fieldset>
    </form>
    {error && <p role="alert" className="error-summary">{error}</p>}{notice && <p role="status">{notice}</p>}
    {confirm && <Confirmation id={'governance-' + path.replace(/\W/g, '-')} title={title + '?'} pending={pending} cancelLabel="Cancel change" confirmLabel="Confirm change" pendingLabel="Saving change..." onCancel={() => { if (!pending) { setConfirm(false); setPassword('') } }} onConfirm={() => void save()}><p>{description}</p></Confirmation>}
  </div>
}
