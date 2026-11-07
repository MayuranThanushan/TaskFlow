import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import type { components } from '../../api/generated'
import { Confirmation } from '../../components/Feedback'
import { useAuth } from './auth-context'

type Status = components['schemas']['MfaStatusDto']
type Setup = components['schemas']['MfaSetupDto']
type Recovery = components['schemas']['MfaRecoveryDto']
export function MfaPage({ client }: { client: ApiClient }) {
  const { session } = useAuth()
  return session.status === 'authenticated' ? <MfaForm key={session.user.id} client={client} /> : null
}
function MfaForm({ client }: { client: ApiClient }) {
  const [status, setStatus] = useState<Status>(), [setup, setSetup] = useState<Setup>(), [codes, setCodes] = useState<string[]>([])
  const [password, setPassword] = useState(''), [code, setCode] = useState(''), [recovery, setRecovery] = useState(''), [useRecovery, setUseRecovery] = useState(false)
  const [error, setError] = useState(''), [notice, setNotice] = useState(''), [pending, setPending] = useState(false), [confirm, setConfirm] = useState<'disable' | 'recovery-codes'>()
  const [now, setNow] = useState(() => Date.now())
  const abort = useRef<AbortController | null>(null), lock = useRef(false), summary = useRef<HTMLDivElement>(null)
  const navigate = useNavigate(), auth = useAuth()
  useEffect(() => { const timer = window.setInterval(() => setNow(Date.now()), 1000); return () => window.clearInterval(timer) }, [])
  const recovering = !!status?.recoveryVerifiedUntilUtc && Date.parse(status.recoveryVerifiedUntilUtc) > now
  useEffect(() => {
    const controller = new AbortController(); abort.current = controller
    void client.request<Status>({ url: '/auth/mfa', signal: controller.signal }).then(setStatus).catch(() => { if (!controller.signal.aborted) setError('MFA settings could not be loaded. Reload this page to try again.') })
    return () => controller.abort()
  }, [client])
  const clearProof = () => { setPassword(''); setCode(''); setRecovery('') }
  async function action(kind: 'setup' | 'confirm' | 'disable' | 'recovery-codes') {
    if (lock.current || !status) return
    setError(''); setNotice('')
    if (!password || (kind !== 'setup' || status.enabled && !recovering) && !(useRecovery && kind !== 'confirm' && kind !== 'recovery-codes' ? recovery.trim() : /^[0-9]{6}$/.test(code))) {
      setError('Enter your current password and the required authenticator or recovery code.'); summary.current?.focus(); return
    }
    lock.current = true; setPending(true); const signal = abort.current?.signal
    try {
      const proof = { currentPassword: password, ...(useRecovery && kind !== 'confirm' && kind !== 'recovery-codes' ? { recoveryCode: recovery.trim() } : code ? { code } : {}) }
      const result = await client.request<Setup | Recovery | void>({ url: '/auth/mfa/' + kind, method: 'post', data: proof, signal })
      if (signal?.aborted) return
      client.forgetSession(); clearProof(); setConfirm(undefined)
      if (kind === 'setup') { setSetup(result as Setup); setUseRecovery(false); setNotice('Add this key to your authenticator app. MFA changes only after you confirm a code below.') }
      else if (kind === 'disable') { setSetup(undefined); await auth.refresh(); void navigate('/login', { replace: true, state: { notice: 'MFA disabled. All sessions were signed out. Sign in again.' } }) }
      else { setSetup(undefined); setCodes((result as Recovery).recoveryCodes); setNotice('Save these recovery codes now. They will not be shown again. Previous recovery codes no longer work. Other sessions were signed out.') }
    } catch (failure) {
      if (signal?.aborted) return
      clearProof(); setConfirm(undefined)
      const problem = asApiError(failure)
      setError(problem.code === 'mfa.email_unverified' ? 'Verify your email before setting up MFA. Open Verify your email below.'
        : problem.code === 'mfa.required_for_administration' ? 'Your company requires MFA for administrators. Restore your authenticator instead of disabling it.'
          : problem.code === 'mfa.enrollment_disabled' ? 'New MFA enrollment is disabled. Contact your operations team.'
            : problem.code === 'mfa.setup_invalid' ? 'Setup expired, was replaced, or the code was invalid or already used. Reopen setup or wait for a fresh code.'
              : problem.code === 'mfa.proof_invalid' ? 'Proof could not be verified. Use your current password and a fresh code. Repeated failures temporarily lock the account.' : problem.message)
      window.setTimeout(() => summary.current?.focus(), 0)
    } finally { lock.current = false; if (!signal?.aborted) setPending(false) }
  }
  async function saved() {
    setCodes([]); setNotice('Recovery codes hidden. Store them securely away from your password.')
    try { const value = await client.request<Status>({ url: '/auth/mfa', signal: abort.current?.signal }); if (!abort.current?.signal.aborted) setStatus(value) } catch { if (!abort.current?.signal.aborted) setError('Reload this page to refresh MFA status.') }
  }
  return <section className="auth-card account-security-card" aria-labelledby="mfa-title">
    <p className="eyebrow">Account protection</p><h1 id="mfa-title">Multi-factor authentication</h1>
    <p className="muted">Use an authenticator app and keep recovery codes offline in case you lose your device.</p>
    {error && <div ref={summary} className="error-summary" role="alert" tabIndex={-1}>{error}</div>}{notice && <p className="notice" role="status">{notice}</p>}
    {!status ? !error && <p role="status">Loading MFA settings...</p> : codes.length ? <div className="mfa-recovery"><h2>Save your recovery codes</h2><p>Each code works once with your password. Leaving or refreshing this page hides this list. Never send these codes to support.</p><ul aria-label="Recovery codes">{codes.map(value => <li key={value}><code>{value.match(/.{1,8}/g)?.join('-')}</code></li>)}</ul><button className="button primary" onClick={() => void saved()}>I saved my recovery codes</button></div> : <>
      <div className="notice"><p>MFA: <strong>{status.enabled ? 'Enabled' : 'Not enabled'}</strong></p><p>Recovery codes remaining: {status.recoveryCodesRemaining}</p>{recovering && <p>Recovery sign-in verified. Within five minutes of sign-in, use your current password to begin replacing the lost authenticator. You do not need a second recovery code.</p>}{status.requiredForAdministration && <p>Your company requires a fresh authenticator proof for administrative changes. Recovery sign-in lets you restore your authenticator first.</p>}</div>
      {!status.enrollmentAvailable ? <p>New MFA enrollment is disabled. Contact your operations team.</p> : <form noValidate onSubmit={e => { e.preventDefault(); void action(setup ? 'confirm' : 'setup') }} aria-busy={pending}><fieldset disabled={pending || !!confirm}>
        {setup && <div className="mfa-setup"><h2>Connect your authenticator</h2><p>Add an account manually in Microsoft Authenticator, Google Authenticator, or another TOTP app. Choose a time-based code with 6 digits and a 30-second interval.</p><p>Account: TaskFlow</p><p className="mfa-secret"><code>{setup.sharedKey}</code></p><p className="field-hint">Setup expires at {new Date(setup.expiresAtUtc).toLocaleTimeString()}. Keep the key private. Enter your password again and a code from the new authenticator.</p></div>}
        <div className="form-field"><label htmlFor="mfa-password">Current password</label><input id="mfa-password" type="password" autoComplete="current-password" maxLength={128} value={password} onChange={e => setPassword(e.target.value)} /></div>
        {(status.enabled || setup) && <>
          {!setup && <label className="mfa-choice"><input type="checkbox" checked={useRecovery} onChange={e => { setUseRecovery(e.target.checked); setCode(''); setRecovery('') }} />Use a recovery code to restore or disable MFA</label>}
          {useRecovery && !setup ? <div className="form-field"><label htmlFor="mfa-recovery-code">Recovery code</label><input id="mfa-recovery-code" type="password" autoComplete="off" maxLength={64} value={recovery} onChange={e => setRecovery(e.target.value)} /></div> : <div className="form-field"><label htmlFor="mfa-code">Authenticator code</label><input id="mfa-code" type="text" inputMode="numeric" autoComplete="one-time-code" maxLength={6} value={code} onChange={e => setCode(e.target.value)} /><p className="field-hint">Use a fresh six-digit code. A code already used for sign-in or another action cannot be reused.</p></div>}
        </>}
        <div className="form-actions"><button className="button primary" type="submit">{pending ? 'Working...' : setup ? 'Confirm authenticator' : status.enabled ? 'Replace authenticator' : 'Set up authenticator'}</button>{setup ? <button type="button" className="button subtle" onClick={() => { setSetup(undefined); clearProof(); setNotice('Setup key hidden. Pending setup expires automatically; the current authenticator is unchanged.') }}>Cancel setup</button> : status.enabled && <><button type="button" className="button subtle" disabled={useRecovery} onClick={() => setConfirm('recovery-codes')}>Replace recovery codes</button><button type="button" className="button danger" disabled={status.requiredForAdministration} onClick={() => setConfirm('disable')}>Disable MFA</button></>}</div>
      </fieldset></form>}
      {confirm && <Confirmation id="mfa-confirm" title={confirm === 'disable' ? 'Disable MFA and sign out all devices?' : 'Replace all recovery codes?'} pending={pending} cancelLabel="Cancel" confirmLabel={confirm === 'disable' ? 'Disable MFA' : 'Replace codes'} pendingLabel="Working..." onCancel={() => setConfirm(undefined)} onConfirm={() => void action(confirm)}><p>{confirm === 'disable' ? 'Authenticator and recovery codes will stop working. Your account will use password-only sign-in.' : 'Unused recovery codes will stop working immediately. Save the new codes when they appear.'}</p></Confirmation>}
    </>}
    <div className="form-actions"><Link to="/account-security">Account security</Link><Link to="/request-verification">Verify your email</Link></div>
  </section>
}
