import { Confirmation } from '../../components/Feedback'
import type { components } from '../../api/generated'
import type { ProjectMember, ProjectMemberList } from './types'
import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import { useApiResource } from '../../hooks/use-api-resource'

type Member = ProjectMember
export function ProjectMembers({ client, projectId, owner }: { client: ApiClient; projectId: string; owner: boolean }) {
  const path = '/projects/' + encodeURIComponent(projectId) + '/members'
  const { result, retry } = useApiResource<ProjectMemberList>(client, path)
  const [userId, setUserId] = useState('')
  const [confirm, setConfirm] = useState<Member | null>(null)
  const [pending, setPending] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const input = useRef<HTMLInputElement>(null)
  const lock = useRef(false)
  const alive = useRef(true)
  useEffect(() => { alive.current = true; return () => { alive.current = false } }, [])
  async function mutate(member?: Member) {
    if (lock.current) return
    if (!member && (!/^[0-9]+$/.test(userId) || BigInt(userId) < 1n || BigInt(userId) > 9223372036854775807n)) {
      setError('Enter a positive user ID within the signed BIGINT range.'); input.current?.focus(); return
    }
    lock.current = true; setPending(true); setError(''); setNotice('')
    try {
      await client.request({ url: member ? path + '/' + encodeURIComponent(member.userId) : path, method: member ? 'delete' : 'post', ...(member ? {} : { data: { userId } satisfies components['schemas']['AddProjectMemberRequest'] }) })
      if (!alive.current) return
      setConfirm(null); if (!member) setUserId('')
      setNotice(member ? 'Member removed. Their active tasks are now unassigned.' : 'Member added.')
      retry(); window.setTimeout(() => input.current?.focus(), 0)
    } catch (failure) {
      if (!alive.current) return
      const problem = asApiError(failure)
      setError(problem.status === 409 ? member ? 'The project owner cannot be removed.' : 'That user is already the owner or a member.'
        : problem.status === 404 ? 'The project or user is no longer available. Refresh the member list and check the user ID.' : problem.message)
    } finally { lock.current = false; if (alive.current) setPending(false) }
  }
  function dismiss() { if (!lock.current) { setConfirm(null); setError(''); window.setTimeout(() => input.current?.focus(), 0) } }
  return <section className="project-members" aria-labelledby="members-heading">
    <h2 id="members-heading">Project members</h2>
    {notice && <p role="status" className="notice">{notice}</p>}
    {error && <p role="alert" className="error-summary">{error}</p>}
    {result.status === 'loading' && <p role="status">Loading members...</p>}
    {result.status === 'error' && <div><p role="alert">{result.error.message}</p><button className="button subtle" onClick={retry}>Refresh members</button></div>}
    {result.status === 'ready' && <ul className="member-list">{result.data.items.map(member => <li key={member.userId}>
      <div><strong>{member.displayName}</strong><span>{member.role} | User ID: {member.userId}</span><span>Joined {new Date(member.joinedAtUtc).toLocaleDateString()}</span></div>
      {owner && member.role === 'Member' && <button className="button subtle" disabled={pending || !!confirm} onClick={() => { setError(''); setConfirm(member) }}>Remove {member.displayName}</button>}
    </li>)}</ul>}
    {owner && <>
      {confirm && <Confirmation key={confirm.userId} id="remove-member-heading" headingLevel={3} title={<>Remove {confirm.displayName}?</>} pending={pending} cancelLabel="Keep member" confirmLabel="Confirm removal" pendingLabel="Removing member..." onCancel={dismiss} onConfirm={() => void mutate(confirm)}><p>They will lose project access. Their active tasks will become unassigned.</p></Confirmation>}
      <form className="member-add" onSubmit={(event: FormEvent) => { event.preventDefault(); void mutate() }}>
        <label htmlFor="member-user-id">Existing user ID</label><p id="member-id-hint" className="field-hint">Ask your teammate for their account ID. New members can view this project and work on its tasks.</p>
        <input ref={input} id="member-user-id" inputMode="numeric" maxLength={19} aria-describedby="member-id-hint" value={userId} disabled={pending || !!confirm} onChange={event => setUserId(event.target.value)} />
        <button className="button primary" disabled={pending || !!confirm} type="submit">{pending && !confirm ? 'Adding member...' : 'Add member'}</button>
      </form>
    </>}
  </section>
}
