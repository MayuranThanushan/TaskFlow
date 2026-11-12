import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import type { components } from '../../api/generated'
import { useApiResource } from '../../hooks/use-api-resource'
import { useAuth } from '../auth/auth-context'
import { useCompany } from '../companies/company-context'
import { Confirmation, FieldError, Pagination, RetryError } from '../../components/Feedback'
type Invitation = components['schemas']['InvitationDto']
type PreviewProject = components['schemas']['ProjectDto']
const date = (s: string) => new Date(s).toLocaleString()
export function InvitationManagerPage({ client }: { client: ApiClient }) {
  const { workspaceId = '', projectId } = useParams(), { role, invitationsEnabled } = useCompany(), { session } = useAuth()
  if (!invitationsEnabled) return <section className="projects-page"><h1>Invitations unavailable</h1><p>Invitations are not enabled on this deployment.</p></section>
  if (role !== 'Owner' && role !== 'Admin') return <section className="projects-page"><h1>Company administration required</h1><p>Only company Owners and Admins can manage email invitations. Project invitations also require current project ownership.</p><Link to={'/companies/' + workspaceId + '/team'}>Back to team</Link></section>
  return projectId ? <ProjectInvitations key={session.user?.id + ':' + workspaceId + ':' + projectId} client={client} workspaceId={workspaceId} projectId={projectId} /> : <InvitationManager key={session.user?.id + ':' + workspaceId} client={client} workspaceId={workspaceId} />
}
function ProjectInvitations({ client, workspaceId, projectId }: { client: ApiClient; workspaceId: string; projectId: string }) {
  const { result, retry } = useApiResource<PreviewProject>(client, '/projects/' + encodeURIComponent(projectId)), { session } = useAuth()
  if (result.status === 'loading') return <p role="status">Checking project access...</p>
  if (result.status === 'error') return <section><h1>Project invitations unavailable</h1><RetryError message="The project is unavailable or your access changed." retry={retry} /></section>
  if (result.data.ownerId !== session.user?.id) return <section><h1>Project owner required</h1><p>Company administration does not grant access to a private project's invitations.</p></section>
  return <InvitationManager client={client} workspaceId={workspaceId} project={{ id: projectId, name: result.data.name }} />
}
function InvitationManager({ client, workspaceId, project }: { client: ApiClient; workspaceId: string; project?: { id: string; name: string } }) {
  const [params, setParams] = useSearchParams(), page = params.get('page') ?? '1'
  const path = '/invitations?page=' + encodeURIComponent(page) + '&pageSize=20' + (project ? '&projectId=' + encodeURIComponent(project.id) : '')
  const { result, retry } = useApiResource<components['schemas']['InvitationPage']>(client, path)
  const { result: policy, retry: reloadPolicy } = useApiResource<components['schemas']['WorkspacePolicyDto']>(client, '/policy')
  const [email, setEmail] = useState(''), [role, setRole] = useState(project ? 'Viewer' : 'Member'), [field, setField] = useState(''), [error, setError] = useState(''), [notice, setNotice] = useState(''), [pending, setPending] = useState(false)
  const [accessLost, setAccessLost] = useState(false)
  const [confirm, setConfirm] = useState<{ row: Invitation; action: 'resend' | 'revoke' } | null>(null)
  const lock = useRef(false), controller = useRef<AbortController | null>(null), emailInput = useRef<HTMLInputElement>(null), summary = useRef<HTMLDivElement>(null)
  const [now, setNow] = useState(() => Date.now())
  useEffect(() => { const abort = new AbortController(); controller.current = abort; const timer = window.setInterval(() => setNow(Date.now()), 1000); return () => { abort.abort(); window.clearInterval(timer) } }, [])
  const reload = () => { setConfirm(null); retry(); reloadPolicy() }
  async function create(event: FormEvent) {
    event.preventDefault(); if (lock.current) return
    setError(''); setField(''); setNotice('')
    if (!/^[^\s@]+@[^\s@]+$/.test(email.trim()) || email.trim().length > 254) { setField('Enter a valid recipient email address.'); emailInput.current?.focus(); return }
    lock.current = true; setPending(true); const signal = controller.current?.signal
    try {
      await client.request<Invitation>({ url: '/invitations', method: 'post', data: { email: email.trim(), role, ...(project ? { projectId: project.id } : {}) }, signal })
      if (!signal?.aborted) { setEmail(''); setNotice('Invitation created and queued. Delivery status is shown below. Access starts only after acceptance.'); setParams({ page: '1' }); reload() }
    } catch (failure) { if (!signal?.aborted) { setError(friendly(failure)); if ([403, 404].includes(asApiError(failure).status ?? 0) && asApiError(failure).code !== 'invitation.email_unverified') setAccessLost(true); setField(asApiError(failure).fields.email?.join(' ') ?? ''); window.setTimeout(() => summary.current?.focus(), 0) } }
    finally { lock.current = false; if (!signal?.aborted) setPending(false) }
  }
  async function act() {
    if (lock.current || !confirm) return
    const selected = confirm; lock.current = true; setPending(true); setError(''); setNotice(''); const signal = controller.current?.signal
    try {
      await client.request({ url: '/invitations/' + selected.row.id + '/' + selected.action, method: 'post', data: { expectedVersion: selected.row.version }, signal })
      if (!signal?.aborted) { setConfirm(null); setNotice(selected.action === 'revoke' ? 'Invitation revoked. Its link no longer grants access.' : 'A new invitation was queued. The previous link no longer works.'); reload() }
    } catch (failure) { if (!signal?.aborted) { setError(friendly(failure)); if ([403, 404].includes(asApiError(failure).status ?? 0)) setAccessLost(true); setConfirm(null); if (asApiError(failure).status === 409) reload(); window.setTimeout(() => summary.current?.focus(), 0) } }
    finally { lock.current = false; if (!signal?.aborted) setPending(false) }
  }
  const title = project ? 'Project invitations' : 'Company invitations'
  const blocked = accessLost || result.status === 'error' && [401, 403, 404].includes(result.error.status ?? 0)
  return <section className="projects-page invite-manager"><div className="page-heading"><div><p className="eyebrow">Team &amp; access{project ? ' / ' + project.name : ''}</p><h1>{title}</h1><p className="muted">Invite the right people with the minimum access they need.</p></div><Link className="button subtle" to={'/companies/' + workspaceId + (project ? '/projects/' + project.id : '/team')}>Back to {project ? 'project' : 'team'}</Link></div>
    {policy.status === 'ready' && <div className="invite-capacity" role="status"><strong>{policy.data.capacity.activeMembers} active {policy.data.capacity.activeMembers === 1 ? 'member' : 'members'}</strong><span>{policy.data.capacity.reservedMemberSeats} reserved {policy.data.capacity.reservedMemberSeats === 1 ? 'seat' : 'seats'}</span><span>{Math.max(0, policy.data.capacity.memberLimit - policy.data.capacity.activeMembers - policy.data.capacity.reservedMemberSeats)} seats available</span></div>}
    {error && <div className="error-summary" tabIndex={-1} ref={summary} role="alert">{error}{error.includes('Verify') && <p><Link to="/request-verification">Verify your email</Link></p>}</div>}{notice && <p className="notice" role="status">{notice}</p>}
    {result.status === 'error' && <RetryError message={blocked ? 'Invitation management is unavailable or your permission changed.' : result.error.message} retry={reload} />}
    {!blocked && <form className="project-card invitation-create" noValidate aria-busy={pending} onSubmit={event => void create(event)}><h2>Invite a teammate</h2><p>{project ? 'New company members join as Guests, with access to this project only. Existing company roles are kept.' : 'Company membership does not grant access to private projects.'}</p><fieldset disabled={pending || result.status !== 'ready'}><div className="invitation-fields"><div className="form-field"><label htmlFor="invite-email">Recipient email</label><input ref={emailInput} id="invite-email" name="email" type="email" autoComplete="off" maxLength={254} value={email} aria-invalid={!!field} aria-describedby={field ? 'invite-email-error' : undefined} onChange={e => { setEmail(e.target.value); setField('') }} /><FieldError id="invite-email-error" errors={field ? [field] : undefined} /></div><div className="form-field"><label htmlFor="invite-role">Role</label><select id="invite-role" aria-describedby="invite-role-hint" value={role} onChange={e => setRole(e.target.value)}>{(project ? ['Viewer', 'Editor'] : ['Member', 'Guest']).map(value => <option key={value}>{value}</option>)}</select><p id="invite-role-hint" className="field-hint">{project ? role === 'Viewer' ? 'Read project content and activity. Changes are not permitted.' : 'Create and edit project tasks. Ownership and membership stay with the project owner.' : role === 'Guest' ? 'Collaborate in explicitly shared projects. Cannot create projects or manage the company.' : 'Create projects and work in projects shared with you. Cannot manage the company.'}</p></div></div><p className="field-hint">The recipient must sign in with this verified email address. Invitations expire after seven days; no access is granted before acceptance.</p><button className="button primary" type="submit">{pending ? 'Working...' : 'Create invitation'}</button></fieldset></form>}
    {result.status === 'loading' ? <p role="status">Loading invitations...</p> : result.status === 'ready' && !blocked && <section className="invitation-history" aria-labelledby="invite-history"><div className="section-heading"><h2 id="invite-history">Invitation history</h2><button className="button subtle" disabled={pending} onClick={reload}>Refresh invitations</button></div>
      {confirm && <Confirmation id="confirm-invitation" title={confirm.action === 'revoke' ? 'Revoke this invitation?' : 'Send a new invitation link?'} pending={pending} cancelLabel="Keep invitation" confirmLabel={confirm.action === 'revoke' ? 'Revoke invitation' : 'Resend invitation'} pendingLabel="Working..." onCancel={() => setConfirm(null)} onConfirm={() => void act()}><p>{confirm.row.email} ? {confirm.row.role}. {confirm.action === 'revoke' ? 'The current link will stop working. No existing memberships are removed.' : 'The old link will stop working. The original expiry stays the same.'}</p></Confirmation>}
      {!result.data.items.length ? <div className="empty-state"><h3>No invitations yet</h3><p>Create an invitation above to bring a teammate into this {project ? 'project' : 'company'}.</p></div> : <ul className="invitation-list">{result.data.items.map(row => <li key={row.id}><div className="invitation-recipient"><strong>{row.email}</strong><span>{row.role} <span aria-hidden="true">/</span> <span className="badge">{row.state}</span></span><span>Expires <time dateTime={row.expiresAtUtc}>{date(row.expiresAtUtc)}</time></span><span>Delivery: {delivery(row.deliveryState)}</span>{row.state === 'Pending' && row.resends < 3 && Date.parse(row.resendAvailableAtUtc) > now && <span>Resend available {date(row.resendAvailableAtUtc)}</span>}{row.resends >= 3 && <span>Resend limit reached</span>}</div>{row.state === 'Pending' && <div className="form-actions"><button className="button subtle" disabled={pending || !!confirm || row.resends >= 3 || Date.parse(row.resendAvailableAtUtc) > now} onClick={() => setConfirm({ row, action: 'resend' })} aria-label={'Resend to ' + row.email}>Resend</button><button className="button subtle" disabled={pending || !!confirm} onClick={() => setConfirm({ row, action: 'revoke' })} aria-label={'Revoke for ' + row.email}>Revoke</button></div>}</li>)}</ul>}
      {result.data.totalPages > 0 && result.data.page > result.data.totalPages && <button className="button subtle" onClick={() => setParams({ page: '1' })}>Return to first page</button>}<Pagination label="Invitation pages" page={result.data.page} totalPages={result.data.totalPages} onPage={p => { setConfirm(null); setParams({ page: String(p) }) }} />
    </section>}
  </section>
}
function friendly(failure: unknown) { const e = asApiError(failure); return e.code === 'invitation.email_unverified' ? 'Verify your email before creating or resending invitations.' : e.code === 'invitation.send_limited' ? 'This company has reached its hourly invitation send limit. Your changes were not sent. Wait up to one hour before trying again; resends count toward the same limit.' : e.code === 'invitation.resend_limited' ? 'Please wait at least one minute between sends. Each invitation permits at most three resends.' : e.code === 'quota.exceeded' ? 'Company capacity is in use or reserved. Review pending invitations and company capacity.' : e.status === 409 ? 'The invitation changed, already exists, or is no longer pending. Refresh and review it before trying again.' : e.message }
function delivery(state: string) { return state === 'Captured' ? 'Preview captured (email not sent)' : state === 'ProviderAccepted' ? 'Accepted by email provider (delivery not confirmed)' : state === 'DeadLetter' ? 'Needs attention. Ask your administrator to check email delivery.' : state === 'Leased' ? 'Processing' : state === 'Pending' ? 'Queued' : 'Cancelled' }
