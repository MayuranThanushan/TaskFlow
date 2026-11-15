import { useEffect, useRef, useState } from 'react'
import type { FormEvent, ReactNode } from 'react'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import type { components } from '../../api/generated'
import { useApiResource } from '../../hooks/use-api-resource'
import { Pagination, RetryError } from '../../components/Feedback'
import { Link } from 'react-router-dom'

type Comment = components['schemas']['TaskCommentDto']
type Page = components['schemas']['CommentPage']
type Candidates = components['schemas']['CommentCandidatesDto']
type History = components['schemas']['CommentHistoryDto']
function text(row: Comment): ReactNode[] {
  const parts: ReactNode[] = []; let offset = 0
  for (const match of (row.body ?? '').matchAll(/@\[([1-9][0-9]{0,18})\]/g)) {
    parts.push(row.body!.slice(offset, match.index)); const person = row.mentions.find(p => p.userId === match[1])
    parts.push(<span className="comment-mention" key={match.index}>{person ? '@' + person.displayName : 'Former collaborator'}</span>); offset = match.index + match[0].length
  }
  parts.push(row.body?.slice(offset)); return parts
}
export function CommentThread({ client, taskId }: { client: ApiClient; taskId: string }) {
  const [page, setPage] = useState(1), [revision, refresh] = useState(0), [unavailable, setUnavailable] = useState(false)
  const base = '/tasks/' + taskId + '/comments'; const { result, retry } = useApiResource<Page>(client, base + '?page=' + page, revision)
  if (unavailable || result.status === 'error' && [403, 404].includes(result.error.status ?? 0)) return <section className="comment-thread"><h2>Comments unavailable</h2><p>Comments may be disabled, or your access has changed.</p></section>
  const reload = () => { refresh(v => v + 1); retry() }
  return <section className="comment-thread" aria-label="Task discussion"><div className="page-heading"><div><h2>Comments</h2><p className="muted">Keep decisions and updates with this task.</p></div><button className="button subtle" onClick={reload}>Refresh comments</button></div>
    {result.status === 'loading' ? <p role="status">Loading comments...</p> : result.status === 'error' ? <RetryError message={result.error.message} retry={retry} /> : <>
      {result.data.canComment ? <CommentForm key={'new:' + revision} client={client} base={base} onSaved={() => { setPage(1); reload() }} onDenied={() => setUnavailable(true)} /> : <p className="field-hint">Your project role allows reading comments.</p>}
      <p role="status">{result.data.totalCount} {result.data.totalCount === 1 ? 'comment' : 'comments'}</p>
      {!result.data.items.length ? <p>No comments on this page.{page > 1 && <button className="button subtle" onClick={() => setPage(1)}>Return to newest comments</button>}</p> : <ol className="comment-list">{result.data.items.map(row => <CommentCard key={row.id + ':' + row.version} client={client} base={base} row={row} onSaved={reload} onDenied={() => setUnavailable(true)} />)}</ol>}
      <Pagination label="Comment pages" page={page} totalPages={result.data.totalPages} onPage={setPage} />
    </>}
  </section>
}
function CommentForm({ client, base, initial, onSaved, onDenied, onCancel }: { client: ApiClient; base: string; initial?: Comment; onSaved: () => void; onDenied: () => void; onCancel?: () => void }) {
  const [body, setBody] = useState(initial?.body ?? ''), [search, setSearch] = useState(''), [query, setQuery] = useState(''), [pending, setPending] = useState(false), [error, setError] = useState('')
  const lock = useRef(false), abort = useRef<AbortController | null>(null), input = useRef<HTMLTextAreaElement>(null)
  useEffect(() => { const c = new AbortController(); abort.current = c; return () => c.abort() }, [])
  async function save(event: FormEvent) {
    event.preventDefault(); if (lock.current) return; lock.current = true; setPending(true); setError(''); let committed = false
    try {
      await client.request({ url: base + (initial ? '/' + initial.id : ''), method: initial ? 'put' : 'post', data: { body, ...(initial ? { expectedVersion: initial.version } : {}) }, signal: abort.current?.signal })
      committed = true; if (!abort.current?.signal.aborted) onSaved()
    } catch (failure) {
      if (!abort.current?.signal.aborted) { const e = asApiError(failure); setError(e.status === 409 ? 'This comment changed. Your draft is kept. Copy it before reloading current comments.' : Object.values(e.fields).flat().join(' ') || e.message); if ([403, 404].includes(e.status ?? 0) && e.code !== 'mfa.administration_required') onDenied() }
    } finally { if (!committed) { lock.current = false; if (!abort.current?.signal.aborted) setPending(false) } }
  }
  const id = 'comment-body-' + (initial?.id ?? 'new')
  return <form className="comment-form" onSubmit={event => void save(event)} aria-label={initial ? 'Edit comment' : 'Add comment'} aria-busy={pending}>
    <label htmlFor={id}>{initial ? 'Edit your comment' : 'Add a comment'}</label><textarea ref={input} id={id} value={body} onChange={e => setBody(e.target.value)} required maxLength={5000} rows={4} disabled={pending} aria-describedby={id + '-hint'} />
    <p className="field-hint" id={id + '-hint'}>Plain text, up to 5000 characters. HTML and links are shown as text. Mention someone by searching below.</p>
    <div className="comment-people"><label htmlFor={id + '-search'}>Find a project collaborator</label><div className="form-actions"><input id={id + '-search'} value={search} onChange={e => setSearch(e.target.value)} maxLength={100} disabled={pending} /><button className="button subtle" type="button" disabled={pending || search.trim().length < 2} onClick={() => setQuery(search.trim())}>Find people</button></div>
      {query && <MentionResults client={client} path={base + '/mention-candidates?search=' + encodeURIComponent(query)} pending={pending} onDenied={onDenied} onChoose={person => { setBody(value => value + (value && !/\s$/.test(value) ? ' ' : '') + '@[' + person.userId + '] '); input.current?.focus() }} />}
    </div>
    {error && <p className="error-summary" role="alert">{error} <Link to="/account-security">Account security</Link></p>}
    <div className="form-actions"><button className="button primary" disabled={pending || !body.trim()}>{pending ? 'Saving comment...' : initial ? 'Save comment' : 'Post comment'}</button>{onCancel && <button className="button subtle" type="button" disabled={pending} onClick={onCancel}>Cancel editing</button>}</div>
  </form>
}
function CommentCard({ client, base, row, onSaved, onDenied }: { client: ApiClient; base: string; row: Comment; onSaved: () => void; onDenied: () => void }) {
  const [editing, setEditing] = useState(false), [confirm, setConfirm] = useState(false), [history, setHistory] = useState(false), [pending, setPending] = useState(false), [error, setError] = useState('')
  const lock = useRef(false), abort = useRef<AbortController | null>(null)
  useEffect(() => { const c = new AbortController(); abort.current = c; return () => c.abort() }, [])
  async function remove() {
    if (lock.current) return; lock.current = true; setPending(true); setError(''); let committed = false
    try { await client.request({ url: base + '/' + row.id, method: 'delete', data: { expectedVersion: row.version }, signal: abort.current?.signal }); committed = true; if (!abort.current?.signal.aborted) onSaved() }
    catch (failure) { if (!abort.current?.signal.aborted) { const e = asApiError(failure); setError(e.status === 409 ? 'This comment changed. Reload comments before deleting.' : e.message); if ([403, 404].includes(e.status ?? 0) && e.code !== 'mfa.administration_required') onDenied() } }
    finally { if (!committed) { lock.current = false; if (!abort.current?.signal.aborted) setPending(false) } }
  }
  return <li className="comment-card"><div className="comment-meta"><strong>{row.authorName}</strong><time dateTime={row.createdAtUtc}>{new Date(row.createdAtUtc).toLocaleString()}</time>{row.editedAtUtc && <span>Edited</span>}</div>
    {editing ? <CommentForm client={client} base={base} initial={row} onSaved={onSaved} onDenied={onDenied} onCancel={() => setEditing(false)} /> : <p className="comment-body">{row.deletedAtUtc ? 'Comment deleted.' : text(row)}</p>}
    {!editing && !confirm && <div className="form-actions">{row.canEdit && <button className="button subtle" onClick={() => setEditing(true)}>Edit comment</button>}{row.canDelete && <button className="button subtle danger" onClick={() => setConfirm(true)}>Delete comment</button>}<button className="button subtle" onClick={() => setHistory(value => !value)} aria-expanded={history}>Revision history</button></div>}
    {confirm && <div role="group" aria-label="Confirm comment deletion"><p>Delete this comment? Its text will be cleared; revision metadata remains.</p><div className="form-actions"><button className="button danger" disabled={pending} onClick={() => void remove()}>Confirm comment deletion</button><button className="button subtle" disabled={pending} onClick={() => setConfirm(false)}>Keep comment</button></div></div>}
    {error && <p role="alert" className="error-summary">{error}</p>}{history && <CommentHistory client={client} path={base + '/' + row.id + '/history'} onDenied={onDenied} />}
  </li>
}
function CommentHistory({ client, path, onDenied }: { client: ApiClient; path: string; onDenied: () => void }) {
  const { result, retry } = useApiResource<History>(client, path)
  useEffect(() => { if (result.status === 'error' && [403, 404].includes(result.error.status ?? 0)) onDenied() }, [result, onDenied])
  return <div className="comment-history"><p className="field-hint">History records who changed the comment and when. Previous text is not retained.</p>{result.status === 'loading' ? <p role="status">Loading revision history...</p> : result.status === 'error' ? <RetryError message={result.error.message} retry={retry} /> : <ol>{result.data.items.map(row => <li key={row.version}>{row.action} by {row.actorName} <span aria-hidden="true">&middot;</span> <time dateTime={row.createdAtUtc}>{new Date(row.createdAtUtc).toLocaleString()}</time></li>)}</ol>}</div>
}

function MentionResults({ client, path, pending, onChoose, onDenied }: { client: ApiClient; path: string; pending: boolean; onChoose: (person: Candidates['items'][number]) => void; onDenied: () => void }) {
  const { result, retry } = useApiResource<Candidates>(client, path)
  useEffect(() => { if (result.status === 'error' && [403, 404].includes(result.error.status ?? 0)) onDenied() }, [result, onDenied])
  return result.status === 'loading' ? <p role="status">Finding collaborators...</p> : result.status === 'error' ? <RetryError message="Could not find collaborators." retry={retry} /> : <div role="group" aria-label="Mention candidates">{result.data.items.length ? result.data.items.map(person => <button key={person.userId} type="button" className="button subtle" disabled={pending} onClick={() => onChoose(person)}>Mention {person.displayName}</button>) : <p>No matching project collaborators.</p>}</div>
}
