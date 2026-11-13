import { useEffect, useRef, useState } from 'react'
import type { FormEvent } from 'react'
import { Link, NavLink, useParams, useSearchParams } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import type { components } from '../../api/generated'
import { Pagination, RetryError } from '../../components/Feedback'
import { useApiResource } from '../../hooks/use-api-resource'

type Page = components['schemas']['NotificationPage']
type Count = components['schemas']['NotificationCountDto']
type Preferences = components['schemas']['NotificationPreferencesDto']
const date = (value: string) => new Date(value).toLocaleString()
function denied(failure: unknown) { const e = asApiError(failure); return [403, 404].includes(e.status ?? 0) && e.code !== 'mfa.administration_required' }

export function NotificationNavLink({ client, workspaceId, revision }: { client: ApiClient; workspaceId: string; revision: number }) {
  const { result } = useApiResource<Count>(client, '/notifications/unread-count', revision)
  const count = result.status === 'ready' ? result.data.unreadCount : undefined
  return <NavLink to={'/companies/' + workspaceId + '/notifications'} aria-label={count ? `Inbox, ${count} unread` : 'Inbox'}>Inbox{count ? <span className="inbox-nav-count" aria-hidden="true">{count > 99 ? '99+' : count}</span> : null}</NavLink>
}

export function NotificationInboxPage({ client, onChanged }: { client: ApiClient; onChanged: () => void }) {
  const [params, setParams] = useSearchParams()
  const [generation, setGeneration] = useState(0)
  const view = params.get('view') ?? 'all', text = params.get('page') ?? '1', page = Number(text)
  const valid = /^[1-9][0-9]*$/.test(text) && page <= 501 && ['all', 'unread'].includes(view)
    && [...params.keys()].every(k => ['view', 'page'].includes(k) && params.getAll(k).length === 1)
  if (!valid) return <section className="projects-page"><h1>Inbox</h1><p role="alert">This inbox filter is not valid.</p><button className="button subtle" onClick={() => setParams({ view: 'all', page: '1' })}>Reset inbox filters</button></section>
  return <Inbox key={generation} client={client} page={page} unreadOnly={view === 'unread'} onChanged={onChanged} onReset={() => setGeneration(value => value + 1)} onView={value => setParams({ view: value, page: '1' })} onPage={value => setParams({ view, page: String(value) })} />
}

function Inbox({ client, page, unreadOnly, onChanged, onReset, onView, onPage }: { client: ApiClient; page: number; unreadOnly: boolean; onChanged: () => void; onReset: () => void; onView: (view: string) => void; onPage: (page: number) => void }) {
  const { workspaceId } = useParams()
  const path = `/notifications?page=${page}&pageSize=20&unreadOnly=${unreadOnly}`
  const { result, retry } = useApiResource<Page>(client, path)
  const [pending, setPending] = useState(false), [error, setError] = useState(''), [notice, setNotice] = useState(''), [unavailable, setUnavailable] = useState(false)
  const lock = useRef(false), controller = useRef<AbortController | null>(null), summary = useRef<HTMLDivElement>(null)
  useEffect(() => { const abort = new AbortController(); controller.current = abort; return () => abort.abort() }, [])
  useEffect(() => { const refresh = () => { retry(); onChanged() }; window.addEventListener('focus', refresh); return () => window.removeEventListener('focus', refresh) }, [retry, onChanged])
  async function write(url: string, method: 'post' | 'patch', data: unknown) {
    if (lock.current) return
    lock.current = true; setPending(true); setError(''); setNotice(''); const signal = controller.current?.signal
    try {
      await client.request({ url, method, data, signal })
      if (!signal?.aborted) { setNotice('Inbox updated.'); retry(); onChanged() }
    } catch (failure) {
      if (!signal?.aborted) { setError(asApiError(failure).message); if (denied(failure)) setUnavailable(true); window.setTimeout(() => summary.current?.focus(), 0) }
    } finally { lock.current = false; if (!signal?.aborted) setPending(false) }
  }
  if (unavailable || result.status === 'error' && denied(result.error)) return <section className="projects-page"><h1>Inbox unavailable</h1><p>Your access may have changed, or notifications are not enabled for this company.</p><div className="form-actions"><button className="button subtle" onClick={onReset}>Reload inbox</button><Link className="button subtle" to="/companies">Choose a company</Link></div></section>
  const unread = result.status === 'ready' ? result.data.items.filter(n => !n.readAtUtc).map(n => n.id) : []
  return <section className="projects-page inbox-page">
    <div className="page-heading"><div><p className="eyebrow">Your company updates</p><h1>Inbox</h1><p className="muted">Assignments, due reminders and daily task digests in one place.</p></div><button className="button subtle" disabled={pending} onClick={() => { setError(''); retry(); onChanged() }}>Refresh inbox</button></div>
    <div className="inbox-toolbar"><div className="inbox-filters" role="group" aria-label="Inbox filters"><button className="button subtle" aria-pressed={!unreadOnly} disabled={pending} onClick={() => onView('all')}>All notifications</button><button className="button subtle" aria-pressed={unreadOnly} disabled={pending} onClick={() => onView('unread')}>Unread</button></div><button className="button subtle" disabled={pending || !unread.length} onClick={() => void write('/notifications/read', 'post', { notificationIds: unread })}>Mark page as read</button></div>
    {error && <div className="error-summary" role="alert" tabIndex={-1} ref={summary}>{error}<p><Link to="/account-security">Account security</Link></p></div>}{notice && <p className="notice" role="status">{notice}</p>}
    {result.status === 'loading' ? <p role="status">Loading inbox...</p> : result.status === 'error' ? <RetryError message={result.error.message} retry={retry} /> : <>
      <p className="inbox-count" role="status">{result.data.unreadCount} unread {result.data.unreadCount === 1 ? 'notification' : 'notifications'}</p>
      {!result.data.items.length ? <div className="empty-state"><h2>{page > 1 && result.data.totalCount ? 'Nothing on this page' : unreadOnly ? 'You’re all caught up' : 'Your inbox is clear'}</h2><p>{unreadOnly ? 'New assignments will appear here when your team needs you.' : 'When a teammate assigns a task to you, you’ll find it here.'}</p>{page > 1 && <button className="button subtle" onClick={() => onPage(1)}>Return to first page</button>}</div> : <ol className="notification-list">{result.data.items.map(row => <li className={row.readAtUtc ? 'is-read' : 'is-unread'} key={row.id}><div className="notification-copy"><span className="badge">{row.readAtUtc ? 'Read' : 'Unread'}</span><h2><Link to={'/companies/' + workspaceId + '/tasks/' + row.taskId}>{row.taskTitle}</Link></h2><p>{row.kind === 'TaskDue' ? 'Due reminder' : row.kind === 'TaskDigest' ? 'Daily digest' : 'Assigned to you'} <span aria-hidden="true">·</span> {row.projectName}</p><span className="field-hint">Delivered in app</span> <time dateTime={row.createdAtUtc}>{date(row.createdAtUtc)}</time></div><button className="button subtle" disabled={pending} aria-label={`Mark ${row.taskTitle} as ${row.readAtUtc ? 'unread' : 'read'}`} onClick={() => void write('/notifications/' + row.id, 'patch', { isRead: !row.readAtUtc })}>Mark as {row.readAtUtc ? 'unread' : 'read'}</button></li>)}</ol>}
      <fieldset disabled={pending}><Pagination label="Inbox pages" page={result.data.page} totalPages={result.data.totalPages} onPage={onPage} /></fieldset>
    </>}
    <PreferencePanel client={client} onUnavailable={setUnavailable} />
  </section>
}

function PreferencePanel({ client, onUnavailable }: { client: ApiClient; onUnavailable: (value: boolean) => void }) {
  const { result, retry } = useApiResource<Preferences>(client, '/notifications/preferences')
  const [notice, setNotice] = useState('')
  const [formRevision, setFormRevision] = useState(0)
  useEffect(() => { let active = true; if (result.status === 'error' && denied(result.error)) queueMicrotask(() => { if (active) onUnavailable(true) }); return () => { active = false } }, [result, onUnavailable])
  return <details className="notification-preferences"><summary>Notification preferences</summary><p>These preferences apply only to you in this company. Existing inbox items remain when notifications are muted.</p>
    {notice && <p className="notice" role="status">{notice}</p>}
    {result.status === 'loading' ? <p role="status">Loading preferences...</p> : result.status === 'error' ? <RetryError message={result.error.message} retry={retry} /> : <PreferencesForm key={formRevision + ':' + result.data.version} client={client} initial={result.data} onUnavailable={onUnavailable} onReload={() => { setNotice(''); setFormRevision(value => value + 1); retry() }} onSaved={() => { setNotice('Notification preferences saved.'); retry() }} />}
  </details>
}
function PreferencesForm({ client, initial, onUnavailable, onReload, onSaved }: { client: ApiClient; initial: Preferences; onUnavailable: (value: boolean) => void; onReload: () => void; onSaved: () => void }) {
  const [inApp, setInApp] = useState(initial.inAppEnabled), [assignments, setAssignments] = useState(initial.taskAssignedEnabled), [pending, setPending] = useState(false), [error, setError] = useState('')
  const [reminders, setReminders] = useState(initial.dueReminderEnabled ?? false), [digest, setDigest] = useState(initial.dailyDigestEnabled ?? false)
  const [zone, setZone] = useState(initial.timeZoneId ?? 'UTC'), [hour, setHour] = useState(initial.digestHour ?? 9)
  const lock = useRef(false), controller = useRef<AbortController | null>(null)
  useEffect(() => { const abort = new AbortController(); controller.current = abort; return () => abort.abort() }, [])
  async function save(event: FormEvent) {
    event.preventDefault(); if (lock.current) return
    lock.current = true; setPending(true); setError(''); const signal = controller.current?.signal
    try {
      await client.request({ url: '/notifications/preferences', method: 'put', data: { inAppEnabled: inApp, taskAssignedEnabled: assignments, dueReminderEnabled: reminders, dailyDigestEnabled: digest, timeZoneId: zone, digestHour: hour, expectedVersion: initial.version }, signal })
      if (!signal?.aborted) onSaved()
    } catch (failure) {
      if (!signal?.aborted) { const e = asApiError(failure); setError(e.status === 409 ? 'Your preferences changed in another tab. Your draft is kept. Reload current preferences before saving again.' : e.message); if (denied(e)) onUnavailable(true) }
    } finally { lock.current = false; if (!signal?.aborted) setPending(false) }
  }
  return <form onSubmit={event => void save(event)} aria-label="Notification preferences" aria-busy={pending}>
    <fieldset disabled={pending}><legend>Where you’re notified</legend><label className="notification-choice"><input type="checkbox" checked={inApp} onChange={e => setInApp(e.target.checked)} />In-app notifications</label><label className="notification-choice"><input type="checkbox" disabled checked={false} readOnly aria-describedby="notification-email-hint" />Email notifications</label><p className="field-hint" id="notification-email-hint">Task-assignment email is not available yet. Account security and invitation email are separate.</p></fieldset>
    <fieldset disabled={pending || !inApp}><legend>What you’re notified about</legend><label className="notification-choice"><input type="checkbox" checked={assignments} onChange={e => setAssignments(e.target.checked)} />Tasks assigned to me</label>
      <label className="notification-choice"><input type="checkbox" checked={reminders} onChange={e => setReminders(e.target.checked)} />Remind me when assigned tasks are due</label>
      <label className="notification-choice"><input type="checkbox" checked={digest} onChange={e => setDigest(e.target.checked)} />Daily digest of overdue tasks and tasks due in the next 24 hours</label>
      <label htmlFor="notification-timezone">Timezone</label><input id="notification-timezone" value={zone} required maxLength={100} onChange={e => setZone(e.target.value)} aria-describedby="notification-schedule-hint" placeholder="Asia/Colombo" />
      <label htmlFor="notification-digest-hour">Daily digest hour</label><select id="notification-digest-hour" value={hour} onChange={e => setHour(Number(e.target.value))}>{Array.from({ length: 24 }, (_, h) => <option key={h} value={h}>{String(h).padStart(2, '0')}:00</option>)}</select>
      <p className="field-hint" id="notification-schedule-hint">Use an IANA timezone such as Asia/Colombo or America/New_York. Reminders and digest task cards appear in your inbox when your company scheduler is running. Scheduled items disappear if the task is completed, reassigned or its deadline changes. Email is separate.</p>
    </fieldset>
    {error && <p className="error-summary" role="alert">{error} <Link to="/account-security">Account security</Link></p>}
    <div className="form-actions"><button className="button primary" disabled={pending}>{pending ? 'Saving...' : 'Save preferences'}</button><button className="button subtle" type="button" disabled={pending} onClick={onReload}>Reload current preferences</button></div>
  </form>
}
