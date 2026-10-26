import { FieldError } from '../../components/Feedback'
import { useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { asApiError } from '../../api/client'
import type { FieldErrors } from '../../api/types'
import { useAuth } from './auth-context'
import { returnPath } from './navigation'

export function AuthPage({ mode }: { mode: 'login' | 'register' }) {
  const registering = mode === 'register'
  const auth = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const state = (location.state ?? {}) as { returnTo?: unknown; notice?: string; registeredEmail?: string }
  const [values, setValues] = useState({ displayName: '', email: state.registeredEmail ?? '', password: '' })
  const [fields, setFields] = useState<FieldErrors>({})
  const [error, setError] = useState('')
  const [pending, setPending] = useState(false)
  const [showPassword, setShowPassword] = useState(false)
  const locked = useRef(false)
  const summary = useRef<HTMLDivElement>(null)
  const notice = state.notice ?? auth.session.message

  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (locked.current) return
    const errors: FieldErrors = {}
    if (registering && (!values.displayName.trim() || values.displayName.trim().length > 100))
      errors.displayName = ['Enter a name between 1 and 100 characters.']
    if (!/^[^\s@]+@[^\s@]+$/.test(values.email.trim()) || values.email.trim().length > 256)
      errors.email = ['Enter a valid email address.']
    if (!values.password || values.password.length > 128) errors.password = ['Enter a password of up to 128 characters.']
    else if (registering && (values.password.length < 12 || !/[A-Z]/.test(values.password) || !/[a-z]/.test(values.password)
      || !/[0-9]/.test(values.password) || !/[^a-zA-Z0-9]/.test(values.password)))
      errors.password = ['Use at least 12 characters with uppercase, lowercase, a number, and a symbol.']
    setFields(errors)
    setError('')
    if (Object.keys(errors).length) {
      const first = event.currentTarget.elements.namedItem(Object.keys(errors)[0]) as HTMLInputElement | null
      first?.focus()
      return
    }
    locked.current = true
    setPending(true)
    try {
      const credentials = { email: values.email.trim(), password: values.password }
      if (registering) {
        await auth.register({ ...credentials, displayName: values.displayName.trim() })
        navigate('/login', { replace: true, state: { registeredEmail: credentials.email,
          returnTo: returnPath(state.returnTo), notice: 'Account created. Sign in to continue.' } })
      } else {
        await auth.signIn(credentials)
        navigate(returnPath(state.returnTo), { replace: true })
      }
    } catch (failure) {
      const problem = asApiError(failure)
      setFields(problem.fields)
      setError(problem.message)
      window.setTimeout(() => summary.current?.focus(), 0)
    } finally { locked.current = false; setPending(false) }
  }

  const field = (name: 'displayName' | 'email' | 'password', label: string, type = 'text') => <div className="form-field">
    <label htmlFor={name}>{label}</label>
    <div className={name === 'password' ? 'password-control' : undefined}><input id={name} name={name} type={name === 'password' && showPassword ? 'text' : type} value={values[name]}
      autoComplete={name === 'password' ? registering ? 'new-password' : 'current-password' : name === 'email' ? 'email' : 'name'}
      maxLength={name === 'displayName' ? 100 : name === 'email' ? 256 : 128}
      aria-invalid={!!fields[name]?.length}
      aria-describedby={fields[name]?.length ? `${name}-error` : name === 'password' && registering ? 'password-hint' : undefined}
      onChange={event => { setValues(previous => ({ ...previous, [name]: event.target.value })); setFields(previous => ({ ...previous, [name]: [] })) }} />{name === 'password' && <button type="button" className="password-toggle" aria-label={showPassword ? 'Hide password' : 'Show password'} aria-pressed={showPassword} onClick={() => setShowPassword(value => !value)}>{showPassword ? 'Hide' : 'Show'}</button>}</div>
    <FieldError id={`${name}-error`} errors={fields[name]} />
  </div>

  return <div className="auth-grid">
    <aside className="auth-story" aria-label="About TaskFlow">
      <p className="eyebrow">A little more clarity.</p>
      <p className="story-title">Make room<br />for good work.</p>
      <p>Bring your projects, tasks, and team into one thoughtful workspace.</p>
      <div className="auth-preview" aria-hidden="true"><div className="auth-preview-title"><span className="preview-window-dot" /> YOUR NEXT CHAPTER</div><strong>Good work starts with a clear plan.</strong><div className="auth-preview-row"><span className="preview-check">&#10003;</span> Set a direction<span className="preview-pill">Complete</span></div><div className="auth-preview-row"><span className="preview-check" /> Bring your team together</div><div className="auth-preview-row"><span className="preview-check" /> Make meaningful progress</div><div className="auth-preview-progress"><span /></div></div>
      <p className="story-footnote">Less chasing. More moving forward.</p>
    </aside>
    <section className="auth-card" aria-labelledby="form-title">
      <p className="eyebrow">{registering ? 'Start with a fresh page' : 'Good to see you again'}</p>
      <h1 id="form-title">{registering ? 'Create your account' : 'Sign in to TaskFlow'}</h1>
      <p className="muted">{registering ? 'A place to organize what matters.' : 'Pick up where you left off.'}</p>
      {notice && !error && <p className="notice" role="status">{notice}</p>}
      {error && <div className="error-summary" role="alert" tabIndex={-1} ref={summary}>{error}</div>}
      <form onSubmit={submit} noValidate aria-busy={pending}>
        <fieldset disabled={pending}>
          {registering && field('displayName', 'Your name')}
          {field('email', 'Email address', 'email')}
          {field('password', 'Password', 'password')}
          {registering && <p id="password-hint" className="field-hint">At least 12 characters, with uppercase, lowercase, a number, and a symbol.</p>}
          <button className="button primary submit" type="submit">{pending ? registering ? 'Creating account…' : 'Signing in…' : registering ? 'Create account' : 'Sign in'}</button>
        </fieldset>
      </form>
      <p className="auth-switch">{registering ? 'Already have an account?' : 'New to TaskFlow?'}{' '}
        <Link to={registering ? '/login' : '/register'} state={{ returnTo: returnPath(state.returnTo) }}>{registering ? 'Sign in' : 'Create an account'}</Link>
      </p>
    </section>
  </div>
}
