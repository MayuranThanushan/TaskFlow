import { useEffect, useMemo, useRef, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import type { components } from '../../api/generated'
import { useAuth } from '../auth/auth-context'
import { SessionCheck } from '../auth/RouteGates'
import { useInvitation } from './invitation-context'
import type { InvitationLink } from './invitation-context'
import { RetryError } from '../../components/Feedback'
type Preview = components['schemas']['InvitationPreviewDto']
export function InvitationAcceptPage({ client, enabled }: { client: ApiClient; enabled: boolean }) {
  const { pending, clear } = useInvitation(), { session } = useAuth()
  if (!enabled) return <section className="auth-card invite-accept"><h1>Invitations unavailable</h1><p>This deployment has not enabled invitations. Contact your company administrator.</p><Link to="/login">Back to sign in</Link></section>
  if (!pending) return <section className="auth-card invite-accept"><h1>Open your invitation</h1><p>Open the original link from your invitation email. For your privacy, invitation links are kept only in this tab and must be reopened after a reload.</p><Link to="/login">Back to sign in</Link></section>
  if (session.status === 'checking' || session.status === 'error') return <SessionCheck />
  if (session.status !== 'authenticated') return <section className="auth-card invite-accept"><p className="eyebrow">An invitation is waiting</p><h1>Join your team</h1><p>Sign in with the email address that received this invitation. You can review the company, project and role before accepting.</p><div className="form-actions"><Link className="button primary" to="/login" state={{ returnTo: '/invitations/accept' }}>Sign in to review</Link><Link className="button subtle" to="/register" state={{ returnTo: '/invitations/accept' }}>Create an account</Link></div><p className="field-hint">New accounts must verify their email. Keep this tab open, or reopen the original invitation after verification.</p></section>
  return <Recipient key={session.user.id + ':' + pending.workspaceId + ':' + pending.invitationId + ':' + pending.generation} client={client} link={pending} clear={clear} />
}
function Recipient({ client, link, clear }: { client: ApiClient; link: InvitationLink; clear: () => void }) {
  const auth = useAuth(), navigate = useNavigate()
  const scoped = useMemo(() => client.forWorkspace(link.workspaceId), [client, link.workspaceId])
  const [preview, setPreview] = useState<Preview>(), [verified, setVerified] = useState<boolean>(), [error, setError] = useState(''), [attempt, setAttempt] = useState(0), [pending, setPending] = useState(false), [accepted, setAccepted] = useState(false)
  const acceptedRef = useRef(false)
  useEffect(() => () => { if (acceptedRef.current) clear() }, [clear])
  const gate = useRef(false), controller = useRef<AbortController | null>(null), summary = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const abort = new AbortController(); controller.current = abort
    let alive = true
    queueMicrotask(async () => {
      if (!alive) return
      setError(''); setPreview(undefined); setVerified(undefined)
      try {
        const state = await client.accountSecurity(); if (!alive) return
        setVerified(state.emailConfirmed)
        if (!state.emailConfirmed) return
        const result = await scoped.request<Preview>({ url: '/invitations/' + link.invitationId + '/preview', method: 'post', data: { token: link.token }, signal: abort.signal })
        if (alive) setPreview(result)
      } catch (failure) { if (alive) setError(message(failure)) }
    })
    return () => { alive = false; abort.abort(); scoped.cancelRequests() }
  }, [client, scoped, link.invitationId, link.token, attempt])
  async function accept() {
    if (gate.current || !preview) return
    gate.current = true; setPending(true); setError('')
    const signal = controller.current?.signal
    try {
      await scoped.request<Preview>({ url: '/invitations/' + link.invitationId + '/accept', method: 'post', data: { token: link.token }, signal })
      if (!signal?.aborted) { acceptedRef.current = true; setAccepted(true) }
    } catch (failure) { if (!signal?.aborted) { if ([403, 404, 409].includes(asApiError(failure).status ?? 0)) setPreview(undefined); setError(message(failure)); window.setTimeout(() => summary.current?.focus(), 0) } }
    finally { gate.current = false; if (!signal?.aborted) setPending(false) }
  }
  async function switchAccount() { if (gate.current) return; gate.current = true; setPending(true); try { await auth.signOut(); void navigate('/login', { replace: true, state: { returnTo: '/invitations/accept', notice: 'Sign in with the email that received the invitation.' } }) } catch (failure) { setError(asApiError(failure).message); setPending(false) } finally { gate.current = false } }
  if (accepted && preview) return <section className="auth-card invite-accept"><p className="eyebrow">You are connected</p><h1>Invitation accepted</h1><p role="status">Your access to {preview.workspaceName} is ready.</p><Link className="button primary" to={'/companies/' + preview.workspaceId + (preview.projectId ? '/projects/' + preview.projectId : '/dashboard')} onClick={clear}>Open {preview.projectId ? 'project' : 'company'}</Link></section>
  return <section className="auth-card invite-accept"><p className="eyebrow">Review your invitation</p><h1>Join a workspace</h1><p className="muted">Signed in as {auth.session.user?.email}</p>
    {verified === false ? <div className="notice" role="status"><p>Verify your account email before accepting this invitation.</p><Link to="/request-verification">Verify your email</Link><button className="button subtle" onClick={() => { void auth.refresh(); setAttempt(a => a + 1) }}>I have verified my email</button></div> : !preview && !error ? <p role="status">Checking invitation...</p> : null}
    {error && <div ref={summary} tabIndex={-1} className="error-summary"><RetryError message={error} retry={() => setAttempt(a => a + 1)} /></div>}
    {preview && <><dl className="invite-preview"><div><dt>Company</dt><dd>{preview.workspaceName}</dd></div>{preview.projectId && <div><dt>Project</dt><dd>{preview.projectName}</dd></div>}<div><dt>Your role</dt><dd>{preview.role}</dd></div><div><dt>Expires</dt><dd><time dateTime={preview.expiresAtUtc}>{new Date(preview.expiresAtUtc).toLocaleString()}</time></dd></div></dl><p>{preview.projectId ? 'Access is limited to this project. New company membership is Guest; any existing company role is kept.' : 'Company membership does not give access to private projects.'}</p><button className="button primary" disabled={pending} onClick={() => void accept()}>{pending ? 'Accepting...' : 'Accept invitation'}</button></>}
    <div className="form-actions"><button className="button subtle" disabled={pending} onClick={() => void switchAccount()}>Use another account</button><Link to="/companies">View my companies</Link><Link to="/login" onClick={clear}>Dismiss invitation</Link></div><p className="field-hint">If the link has expired or been revoked, ask its sender for a new invitation.</p>
  </section>
}
function message(failure: unknown) { const problem = asApiError(failure); return problem.status === 404 || problem.status === 409 ? 'This invitation is unavailable for this account. It may be expired, revoked, already used, or intended for another email.' : problem.message }
