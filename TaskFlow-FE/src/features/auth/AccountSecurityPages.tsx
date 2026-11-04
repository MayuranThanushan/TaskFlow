import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { asApiError } from '../../api/client'
import type { ApiClient } from '../../api/client'
import { useAuth } from './auth-context'
import { FieldError } from '../../components/Feedback'

type Capabilities = { emailFlowsEnabled: boolean; requireConfirmedEmail: boolean }
type Security = { emailConfirmed: boolean; reauthenticatedUntilUtc: string | null }
const passwordHint = 'Use at least 12 characters with uppercase, lowercase, a number, and a symbol.'
const strong = (p: string) => p.length >= 12 && p.length <= 128 && /[A-Z]/.test(p) && /[a-z]/.test(p) && /[0-9]/.test(p) && /[^a-zA-Z0-9]/.test(p)

export function AccountActionPage({ client, mode }: { client: ApiClient; mode: 'forgot' | 'resend' | 'confirm' | 'reset' }) {
  const location = useLocation(), navigate = useNavigate(), auth = useAuth()
  const [link] = useState(() => { const parts = new URLSearchParams(location.hash.slice(1)); return { userId: parts.get('userId') ?? '', token: parts.get('token') ?? '' } })
  const [capabilities, setCapabilities] = useState<Capabilities | null>(null)
  const [email, setEmail] = useState(''), [password, setPassword] = useState(''), [repeat, setRepeat] = useState('')
  const [pending, setPending] = useState(false), [done, setDone] = useState(false), [error, setError] = useState('')
  const [fields, setFields] = useState<Record<string, string[]>>({})
  const lock = useRef(false), summary = useRef<HTMLDivElement>(null)
  const request = mode === 'forgot' || mode === 'resend'
  const hasLink = /^[1-9][0-9]{0,18}$/.test(link.userId) && link.token.length >= 20 && link.token.length <= 4096
  useEffect(() => { if (location.hash) void navigate(location.pathname + location.search, { replace: true }) }, [location.hash, location.pathname, location.search, navigate])
  useEffect(() => { let alive = true; client.accountCapabilities().then(value => { if (alive) setCapabilities(value) }).catch(() => { if (alive) setError('Account services could not be loaded. Reload this page to try again.') }); return () => { alive = false } }, [client])
  const titles = { forgot: 'Reset your password', resend: 'Verify your email', confirm: 'Confirm your email', reset: 'Choose a new password' }
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); if (lock.current) return
    const invalid: Record<string, string[]> = {}
    if (request && (!/^[^\s@]+@[^\s@]+$/.test(email.trim()) || email.trim().length > 254)) invalid.email = ['Enter a valid email address.']
    if (mode === 'reset' && !strong(password)) invalid.newPassword = [passwordHint]
    if (mode === 'reset' && password !== repeat) invalid.repeat = ['Passwords must match.']
    setFields(invalid); setError('')
    if (Object.keys(invalid).length) { (event.currentTarget.elements.namedItem(Object.keys(invalid)[0]) as HTMLInputElement | null)?.focus(); return }
    lock.current = true; setPending(true)
    try {
      if (request) await client.requestAccountMail(email.trim(), mode === 'forgot')
      else if (mode === 'confirm') await client.confirmEmail(link)
      else await client.resetPassword({ ...link, newPassword: password })
      setPassword(''); setRepeat(''); setDone(true)
      if (!request) { client.forgetSession(); await auth.refresh() }
    } catch (failure) {
      const problem = asApiError(failure); setFields(problem.fields)
      setError(problem.fields.token ? 'This link is invalid or expired. Request a new one.' : problem.status === 503 ? 'Email account services are currently unavailable. Contact your administrator.' : problem.status === 429 ? problem.message : problem.fields.newPassword?.join(' ') ?? problem.message)
      window.setTimeout(() => summary.current?.focus(), 0)
    } finally { lock.current = false; setPending(false) }
  }
  return <section className="auth-card account-security-card" aria-labelledby="account-action-title">
    <p className="eyebrow">Account security</p><h1 id="account-action-title">{titles[mode]}</h1>
    <p className="muted">{request ? 'Enter your account email. If it is eligible, we will prepare the next step.' : mode === 'confirm' ? 'Confirm to verify the email address associated with this link. You will then sign in again.' : 'Choose a unique password. Every existing session will be signed out.'}</p>
    {error && <div className="error-summary" role="alert" tabIndex={-1} ref={summary}>{error}</div>}
    {done ? <div className="notice" role="status">{request ? 'If the address is eligible, a link will be prepared. Check your inbox or contact your administrator. Please wait before requesting another.' : mode === 'confirm' ? 'Email verified. Sign in to continue.' : 'Password reset. Sign in with your new password.'}</div>
      : capabilities && !capabilities.emailFlowsEnabled ? <p className="notice" role="status">Email account services are currently unavailable. Contact your administrator.</p>
      : !request && !hasLink ? <p className="error-summary" role="alert">This link is incomplete. Request a new one.</p>
      : <form onSubmit={submit} noValidate aria-busy={pending}><fieldset disabled={pending || !capabilities}>
        {request && <div className="form-field"><label htmlFor="account-email">Email address</label><input id="account-email" name="email" type="email" autoComplete="email" maxLength={254} value={email} aria-invalid={!!fields.email} aria-describedby={fields.email ? 'account-email-error' : undefined} onChange={e => setEmail(e.target.value)} /><FieldError id="account-email-error" errors={fields.email} /></div>}
        {mode === 'reset' && <><div className="form-field"><label htmlFor="new-password">New password</label><input id="new-password" name="newPassword" type="password" autoComplete="new-password" maxLength={128} value={password} aria-invalid={!!fields.newPassword} aria-describedby={fields.newPassword ? 'new-password-hint new-password-error' : 'new-password-hint'} onChange={e => setPassword(e.target.value)} /><p id="new-password-hint" className="field-hint">{passwordHint}</p><FieldError id="new-password-error" errors={fields.newPassword} /></div><div className="form-field"><label htmlFor="repeat-password">Confirm new password</label><input id="repeat-password" name="repeat" type="password" autoComplete="new-password" maxLength={128} value={repeat} aria-invalid={!!fields.repeat} aria-describedby={fields.repeat ? 'repeat-error' : undefined} onChange={e => setRepeat(e.target.value)} /><FieldError id="repeat-error" errors={fields.repeat} /></div></>}
        <button type="submit" className="button primary">{pending ? 'Working...' : request ? 'Request link' : mode === 'confirm' ? 'Verify email' : 'Reset password'}</button>
      </fieldset></form>}
    <div className="form-actions"><Link to="/login">Back to sign in</Link>{!request && <Link to={mode === 'confirm' ? '/request-verification' : '/forgot-password'}>Request a new link</Link>}</div>
  </section>
}

export function AccountSecurityPage({ client }: { client: ApiClient }) {
  const { session } = useAuth()
  return session.status === 'authenticated' ? <AccountSecurityForm key={session.user.id} client={client} /> : null
}
function AccountSecurityForm({ client }: { client: ApiClient }) {
  const [state, setState] = useState<Security | null>(null), [error, setError] = useState(''), [notice, setNotice] = useState('')
  const [current, setCurrent] = useState(''), [password, setPassword] = useState(''), [repeat, setRepeat] = useState('')
  const [fields, setFields] = useState<Record<string, string[]>>({}), [pending, setPending] = useState(false)
  const abort = useRef<AbortController | null>(null)
  useEffect(() => { const controller = new AbortController(); abort.current = controller; return () => controller.abort() }, [])
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const interval = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(interval) }, [])
  const lock = useRef(false), summary = useRef<HTMLDivElement>(null), navigate = useNavigate(), auth = useAuth()
  useEffect(() => { let alive = true; client.accountSecurity().then(value => { if (alive) setState(value) }).catch(() => { if (alive) setError('Security settings could not be loaded. Reload this page to try again.') }); return () => { alive = false } }, [client])
  const submit = async (event: FormEvent<HTMLFormElement>, proof: boolean) => {
    event.preventDefault(); if (lock.current) return
    const invalid: Record<string, string[]> = {}
    if (!current) invalid.currentPassword = ['Enter your current password.']
    if (!proof && !strong(password)) invalid.newPassword = [passwordHint]
    if (!proof && password !== repeat) invalid.repeat = ['Passwords must match.']
    setFields(invalid); setError(''); setNotice('')
    if (Object.keys(invalid).length) { (event.currentTarget.elements.namedItem(Object.keys(invalid)[0]) as HTMLInputElement | null)?.focus(); return }
    lock.current = true; setPending(true)
    const signal = abort.current?.signal
    try {
      if (proof) { await client.reauthenticate(current, signal); if (signal?.aborted) return; client.forgetSession(); setState(await client.accountSecurity()); setNotice('Identity confirmed for five minutes.'); setCurrent('') }
      else { await client.changePassword(current, password, signal); if (signal?.aborted) return; setCurrent(''); setPassword(''); setRepeat(''); client.forgetSession(); await auth.refresh(); void navigate('/login', { replace: true, state: { notice: 'Password changed. All sessions were signed out. Sign in with your new password.' } }) }
    } catch (failure) { if (signal?.aborted) return; const problem = asApiError(failure); setFields(problem.fields); setError(problem.fields.currentPassword?.join(' ') ?? problem.fields.newPassword?.join(' ') ?? problem.message); window.setTimeout(() => summary.current?.focus(), 0) }
    finally { lock.current = false; setPending(false) }
  }
  return <section className="auth-card account-security-card" aria-labelledby="account-security-title"><p className="eyebrow">Your account</p><h1 id="account-security-title">Account security</h1>
    <p className="muted">Manage your password and confirm your identity for sensitive actions.</p>
    {state && <div className="notice"><p>Email: {state.emailConfirmed ? 'Verified' : 'Not verified'}</p>{!state.emailConfirmed && <Link to="/request-verification">Request verification</Link>}<p>Recent identity proof: {state.reauthenticatedUntilUtc && Date.parse(state.reauthenticatedUntilUtc) > now ? 'Active for up to five minutes' : 'Not active'}</p></div>}
    {error && <div className="error-summary" role="alert" tabIndex={-1} ref={summary}>{error}</div>}{notice && <p className="notice" role="status">{notice}</p>}
    <form onSubmit={e => void submit(e, (e.nativeEvent as SubmitEvent).submitter?.id === 'confirm-identity')} noValidate aria-busy={pending}><fieldset disabled={pending}>
      <div className="form-field"><label htmlFor="current-password">Current password</label><input id="current-password" name="currentPassword" type="password" autoComplete="current-password" maxLength={128} value={current} aria-invalid={!!fields.currentPassword} aria-describedby={fields.currentPassword ? 'current-error' : undefined} onChange={e => setCurrent(e.target.value)} /><FieldError id="current-error" errors={fields.currentPassword} /></div>
      <div className="form-field"><label htmlFor="change-password">New password</label><input id="change-password" name="newPassword" type="password" autoComplete="new-password" maxLength={128} value={password} aria-invalid={!!fields.newPassword} aria-describedby={fields.newPassword ? 'change-password-hint change-password-error' : 'change-password-hint'} onChange={e => setPassword(e.target.value)} /><p id="change-password-hint" className="field-hint">{passwordHint}</p><FieldError id="change-password-error" errors={fields.newPassword} /></div>
      <div className="form-field"><label htmlFor="confirm-password">Confirm new password</label><input id="confirm-password" name="repeat" type="password" autoComplete="new-password" maxLength={128} value={repeat} aria-invalid={!!fields.repeat} aria-describedby={fields.repeat ? 'change-repeat-error' : undefined} onChange={e => setRepeat(e.target.value)} /><FieldError id="change-repeat-error" errors={fields.repeat} /></div>
      <p className="field-hint">Changing your password signs out every device. You will need to sign in again.</p>
      <div className="form-actions"><button type="submit" className="button primary">{pending ? 'Working...' : 'Change password'}</button><button id="confirm-identity" type="submit" className="button subtle">Confirm identity only</button></div>
    </fieldset></form><p><Link to="/profile">Back to profile</Link></p>
  </section>
}
