import { useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { Profile } from '../../api/types'
import { asApiError } from '../../api/client'
import { useAuth } from '../auth/auth-context'

export function ProfilePage() {
  const { session } = useAuth()
  return session.status === 'authenticated' ? <ProfileForm key={session.user.id} user={session.user} /> : null
}

function ProfileForm({ user }: { user: Profile }) {
  const { updateProfile } = useAuth()
  const [name, setName] = useState(user.displayName)
  const [field, setField] = useState('')
  const [error, setError] = useState('')
  const [saved, setSaved] = useState(false)
  const [pending, setPending] = useState(false)
  const lock = useRef(false)
  const input = useRef<HTMLInputElement>(null)
  const submit = async (event: FormEvent) => {
    event.preventDefault()
    if (lock.current) return
    setSaved(false); setError(''); setField('')
    const value = name.trim()
    if (!value || value.length > 100) { setField('Enter a name between 1 and 100 characters.'); input.current?.focus(); return }
    lock.current = true; setPending(true)
    try {
      if (await updateProfile(value)) { setName(value); setSaved(true) }
    } catch (failure) {
      const problem = asApiError(failure)
      setField(problem.fields.displayName?.join(' ') ?? '')
      setError(problem.status === 409 ? 'Your profile changed while saving. Reload this page and try again.' : problem.message)
    } finally { lock.current = false; setPending(false) }
  }
  return <section className="auth-card profile-card" aria-labelledby="profile-title">
    <p className="eyebrow">Your account</p><h1 id="profile-title">Profile</h1>
    <p className="muted">Choose the name your project teammates see.</p>
    <p>Account ID: {user.id}</p><p>Email address: {user.email}</p>
    {error && <p className="error-summary" role="alert">{error}</p>}
    {saved && <p className="notice" role="status">Profile saved.</p>}
    <form onSubmit={submit} noValidate aria-busy={pending}><fieldset disabled={pending}>
      <div className="form-field"><label htmlFor="displayName">Display name</label>
        <input ref={input} id="displayName" name="displayName" autoComplete="name" maxLength={100} value={name}
          aria-invalid={!!field} aria-describedby={field ? 'displayName-error' : 'displayName-hint'}
          onChange={event => { setName(event.target.value); setField(''); setSaved(false) }} />
        <p id="displayName-hint" className="field-hint">Use 1 to 100 characters.</p>
        {field && <p id="displayName-error" className="field-error">{field}</p>}
      </div>
      <button className="button primary" type="submit">{pending ? 'Saving...' : 'Save profile'}</button>
    </fieldset></form>
  </section>
}
