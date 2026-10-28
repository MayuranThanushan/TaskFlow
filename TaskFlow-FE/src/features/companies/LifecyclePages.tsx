import { useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import type { components } from '../../api/generated'
import { useApiResource } from '../../hooks/use-api-resource'
import { useAuth } from '../auth/auth-context'
import { Pagination, RetryError, TableRegion } from '../../components/Feedback'
import { GovernanceAction } from './GovernanceAction'
import type { Company } from './CompanyPages'

type Member = components['schemas']['WorkspaceMemberDto']
export function CompanyTeamPage({ client, company, refreshCompany }: { client: ApiClient; company: Company; refreshCompany: () => void }) {
  const { session } = useAuth()
  const navigate = useNavigate()
  const [target, setTarget] = useState('')
  return <section className="projects-page"><div className="page-heading"><div><p className="eyebrow">{company.name}</p><h1>Company team</h1><p className="muted">Company membership does not grant access to private projects.</p></div></div>
    {company.role === 'Guest' ? <p>As a Guest, you can see collaborators inside projects shared with you.</p> : <TeamDirectory client={client} company={company} refresh={refreshCompany} />}
    {company.role === 'Owner' && <GovernanceAction client={client} path="/ownership" method="post" title="Transfer company ownership" data={{ userId: target }} passwordRequired description="The selected active non-Guest member becomes Owner. You become Admin and keep ownership of your private projects until you transfer them separately." onSuccess={refreshCompany}>
      <label>New company owner ID<input inputMode="numeric" pattern="[1-9][0-9]*" maxLength={19} required value={target} onChange={e => setTarget(e.target.value)} /></label>
    </GovernanceAction>}
    <GovernanceAction client={client} path="/leave" method="post" title="Leave company" data={{}} passwordRequired={company.role === 'Owner'} description="You will lose access to this company. Active assignments will be cleared and project grants revoked. Transfer active projects you own first. The last active Owner cannot leave." onSuccess={() => void navigate('/companies', { replace: true })} />
    <p className="field-hint">Your account ID: {session.user?.id}. Invitations and recovery of projects owned by a suspended member are separate workflows.</p>
  </section>
}
function TeamDirectory({ client, company, refresh }: { client: ApiClient; company: Company; refresh: () => void }) {
  const [params, setParams] = useSearchParams()
  const page = params.get('page') ?? '1'
  const { result, retry } = useApiResource<components['schemas']['WorkspaceMemberPage']>(client, '/members?page=' + encodeURIComponent(page))
  const { session } = useAuth()
  return <>{result.status === 'loading' ? <p role="status">Loading company members...</p> : result.status === 'error' ? <RetryError message={result.error.message} retry={retry} /> : <>
    <ul className="member-list">{result.data.items.map(member => <li className="governance-member" key={member.userId}><div><strong>{member.displayName}</strong><span>{member.role} · {member.state} · ID {member.userId}</span></div>
      {(company.role === 'Owner' || company.role === 'Admin' && (member.role === 'Member' || member.role === 'Guest')) && member.role !== null && member.state !== null && member.userId !== session.user?.id && member.state !== 'Removed' && <MemberControls key={[member.role, member.state].join(':')} client={client} member={member} owner={company.role === 'Owner'} refresh={() => { retry(); refresh() }} />}
    </li>)}</ul><Pagination label="Company member pages" page={result.data.page} totalPages={result.data.totalPages} onPage={p => setParams({ page: String(p) })} />
  </>}</>
}
function MemberControls({ client, member, owner, refresh }: { client: ApiClient; member: Member; owner: boolean; refresh: () => void }) {
  const [role, setRole] = useState(member.role ?? 'Member')
  const [state, setState] = useState<'Active' | 'Suspended' | 'Removed'>(member.state === 'Suspended' ? 'Active' : 'Suspended')
  return <div className="member-governance">
    {member.state === 'Active' && <GovernanceAction client={client} path={'/members/' + member.userId + '/role'} method="patch" title={'Change role for ' + member.displayName} data={{ role }} passwordRequired={role === 'Owner' || member.role === 'Owner'} description={'Change company role from ' + member.role + ' to ' + role + '. Private project access stays separate. Owner changes require password verification.'} onSuccess={refresh}>
      <label>Company role<select value={role} onChange={e => setRole(e.target.value as NonNullable<Member['role']>)}>{(owner ? ['Owner', 'Admin', 'Member', 'Guest'] : ['Member', 'Guest']).map(r => <option key={r}>{r}</option>)}</select></label>
    </GovernanceAction>}
    {member.role !== 'Owner' && <GovernanceAction client={client} path={'/members/' + member.userId + '/state'} method="patch" title={'Change access for ' + member.displayName} data={{ state }} description={'Set membership to ' + state + '. Suspension clears active task assignments. Removal also revokes project grants and requires owned projects to be transferred first. Reactivation never restores cleared assignments.'} onSuccess={refresh}>
      <label>Membership state<select value={state} onChange={e => setState(e.target.value as typeof state)}>{['Active', 'Suspended', 'Removed'].map(s => <option key={s}>{s}</option>)}</select></label>
    </GovernanceAction>}
  </div>
}
export function CompanyAuditPage({ client }: { client: ApiClient }) {
  const [params, setParams] = useSearchParams()
  const { result, retry } = useApiResource<components['schemas']['WorkspaceAuditPage']>(client, '/audit?page=' + encodeURIComponent(params.get('page') ?? '1'))
  return <section className="projects-page"><div className="page-heading"><div><p className="eyebrow">Governance history</p><h1>Company audit</h1><p className="muted">Stable actor, target and project IDs. Private project names and content are excluded.</p></div></div>
    {result.status === 'loading' ? <p role="status">Loading company audit...</p> : result.status === 'error' ? <RetryError message={result.error.message} retry={retry} /> : <>
      <TableRegion label="Company audit table"><table className="task-table"><caption className="sr-only">Company governance events</caption><thead><tr><th scope="col">When</th><th scope="col">Action</th><th scope="col">Actor ID</th><th scope="col">Target ID</th><th scope="col">Project ID</th><th scope="col">Change</th></tr></thead><tbody>{result.data.items.map(event => <tr key={event.id}><td><time dateTime={event.createdAtUtc}>{new Date(event.createdAtUtc).toLocaleString()}</time></td><td>{event.action}</td><td>{event.actorUserId}</td><td>{event.targetUserId}</td><td>{event.projectId ?? '—'}</td><td>{event.fromRole ?? '—'} → {event.toRole ?? '—'}</td></tr>)}</tbody></table></TableRegion>
      {result.data.totalCount === 0 && <p>No governance events recorded.</p>}<Pagination label="Company audit pages" page={result.data.page} totalPages={result.data.totalPages} onPage={page => setParams({ page: String(page) })} />
    </>}
  </section>
}
