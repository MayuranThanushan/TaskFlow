import { viewLink } from './view-link'
import type { components } from '../../api/generated'
import { useEffect, useRef, useState } from 'react'
import type { ApiClient } from '../../api/client'
import { ApiError, asApiError } from '../../api/client'
import { useCompany } from '../companies/company-context'
import { Link, useNavigate, useParams, useSearchParams } from '../companies/navigation'
import { useApiResource } from '../../hooks/use-api-resource'
import './views.css'
export type SavedView = components['schemas']['SavedViewDto']
type Preference = components['schemas']['ViewPreferenceDto']
type ViewPage = components['schemas']['SavedViewPage']
export function SavedViews({ client, surface, projectId }: { client: ApiClient; surface: 'work' | 'tasks' | 'board'; projectId?: string }) {
  if (!client.resourceKey.startsWith('workspace:')) return null
  return <ViewControls key={client.resourceKey + ':' + surface + ':' + projectId} client={client} surface={surface} projectId={projectId} />
}
function ViewControls({ client, surface, projectId }: { client: ApiClient; surface: string; projectId?: string }) {
  const [params] = useSearchParams(), navigate = useNavigate(), { role } = useCompany(), { workspaceId } = useParams()
  const context = surface === 'work' ? 'work' : surface + ':' + projectId
  const [open, setOpen] = useState(false), [views, setViews] = useState<ViewPage>(), [page, setPage] = useState(1), [revision, setRevision] = useState(0)
  const [preference, setPreference] = useState<Preference>(), [name, setName] = useState(''), [shared, setShared] = useState(false), [selected, setSelected] = useState(''), [pending, setPending] = useState(false), [message, setMessage] = useState(''), [error, setError] = useState(''), [confirmDelete, setConfirmDelete] = useState(false)
  const initial = useRef(params.toString()), applied = useRef(false), lock = useRef(false), alive = useRef(false), abort = useRef<AbortController | null>(null)
  useEffect(() => { alive.current = true; return () => { alive.current = false; abort.current?.abort() } }, [])
  useEffect(() => {
    const controller = new AbortController(); let active = true
    void client.request<Preference>({ url: '/view-preferences/' + encodeURIComponent(context), signal: controller.signal }).then(async p => {
      if (!active) return; setPreference(p)
      if (!p.available) { setMessage('Your saved default is no longer available. Choose another view.'); return }
      if (!applied.current && !initial.current && p.savedViewId) {
        applied.current = true
        const view = await client.request<SavedView>({ url: '/views/' + p.savedViewId, signal: controller.signal })
        if (active && (view.surface !== surface || view.projectId !== (projectId ?? null))) { setMessage('Your saved default changed its target. Choose another view.'); return }
        if (active) { void navigate(viewLink(view), { replace: true }); setMessage('Your saved default was applied.') }
      }
    }).catch(() => { /* The normal view remains usable if preferences are unavailable. */ })
    return () => { active = false; controller.abort() }
  // A preference is read only on entry; Reset must not immediately reapply it.
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [client, context])
  useEffect(() => {
    if (!open) return
    const controller = new AbortController(); let active = true
    void client.request<ViewPage>({ url: '/views?page=' + page + '&pageSize=20', signal: controller.signal }).then(data => { if (active) setViews(data) }).catch(e => { if (active) setError(asApiError(e).message) })
    return () => { active = false; controller.abort() }
  }, [client, open, page, revision])
  const selectedView = views?.items.find(v => v.id === selected)
  const run = async (operation: (signal: AbortSignal) => Promise<void>) => {
    if (lock.current) return
    lock.current = true; setPending(true); setError(''); setMessage(''); const controller = new AbortController(); abort.current = controller
    try { await operation(controller.signal) } catch (e) { if (alive.current) { const problem = asApiError(e); setError(problem.status === 409 ? 'This view changed. Reload saved views and review before saving again.' : problem.status === 404 ? 'This view or project is no longer available.' : problem.message) } }
    finally { lock.current = false; if (alive.current) setPending(false) }
  }
  const definition = () => {
    const filters: Record<string,string> = {}
    for (const [key, value] of params) { if (key === 'page' || key === 'projectId') continue; if (key in filters) throw new Error('Reset repeated filters before saving.'); filters[key] = value }
    return { name: name.trim(), surface, projectId: projectId ?? params.get('projectId'), shared, filters }
  }
  const save = (replace: boolean) => void run(async signal => {
    const data = definition()
    const view = await client.request<SavedView>({ url: '/views' + (replace ? '/' + selected : ''), method: replace ? 'put' : 'post', signal, data: { ...data, ...(replace ? { expectedVersion: selectedView?.version } : {}) } })
    if (alive.current) { setMessage('View saved.'); setSelected(view.id); setRevision(r => r + 1) }
  })
  const setDefault = (id: string | null) => void run(async signal => {
    const current = await client.request<Preference>({ url: '/view-preferences/' + encodeURIComponent(context), signal })
    // Never overwrite a preference changed since this screen loaded.
    if (preference && current.version !== preference.version) { setPreference(current); throw new ApiError('Preference changed.', 409) }
    const saved = await client.request<Preference>({ url: '/view-preferences/' + encodeURIComponent(context), method: 'put', signal, data: { expectedVersion: current.version, savedViewId: id } })
    if (alive.current) { setPreference(saved); setMessage(id ? 'Default saved for this screen.' : 'Default cleared.') }
  })
  return <details className="saved-views" onToggle={e => setOpen(e.currentTarget.open)}><summary>Saved views & preferences</summary><div className="saved-views-body">
    <p className="field-hint">Personal views are visible only to you. Company-shared views are visible to members who can access their target project. Owners and admins manage shared views. Saved filters grant no access.</p>
    {message && <p role="status">{message}</p>}{error && <p role="alert" className="error-summary">{error}</p>}
    <label htmlFor={'saved-choice-' + surface}>Saved view</label><select id={'saved-choice-' + surface} value={selected} disabled={pending} onChange={e => { setSelected(e.target.value); const v = views?.items.find(v => v.id === e.target.value); setName(v?.name ?? ''); setShared(v?.shared ?? false); setConfirmDelete(false) }}><option value="">Choose a saved view</option>{views?.items.map(v => <option key={v.id} value={v.id}>{v.name} ({v.shared ? 'Company' : 'Personal'} / {v.surface})</option>)}</select>
    <div className="form-actions"><button className="button subtle" disabled={pending || page <= 1} onClick={() => setPage(p => p - 1)}>Previous saved views</button><button className="button subtle" disabled={pending || !views || page >= views.totalPages} onClick={() => setPage(p => p + 1)}>Next saved views</button><button className="button subtle" disabled={pending} onClick={() => { setSelected(''); setConfirmDelete(false); setError(''); setRevision(r => r + 1) }}>Reload saved views</button></div>
    <div className="form-actions"><button className="button subtle" disabled={!selected || pending} onClick={() => void run(async signal => { const v = await client.request<SavedView>({ url: '/views/' + selected, signal }); if (alive.current) { void navigate(viewLink(v)); setMessage('Saved view opened.') } })}>Open saved view</button>{selected && <Link className="text-link" to={'/views/' + selected}>Share link</Link>}<button className="button subtle" disabled={pending || !selectedView || selectedView.surface !== surface || selectedView.projectId !== (projectId ?? null)} onClick={() => setDefault(selected)}>Use as default</button><button className="button subtle" disabled={pending || !preference?.savedViewId} onClick={() => setDefault(null)}>Clear default</button></div>
    <form onSubmit={e => { e.preventDefault(); save(false) }}><label htmlFor={'saved-name-' + surface}>View name</label><input id={'saved-name-' + surface} value={name} onChange={e => setName(e.target.value)} required maxLength={100} disabled={pending} />{['Owner','Admin'].includes(role) && <label className="saved-share"><input type="checkbox" checked={shared} onChange={e => setShared(e.target.checked)} disabled={pending} /> Share with company</label>}<div className="form-actions"><button className="button primary" disabled={pending || !name.trim()}>Save current view as new</button><button type="button" className="button subtle" disabled={pending || !selectedView?.canManage || !name.trim()} onClick={() => save(true)}>Replace selected with current view</button><button type="button" className="button subtle" disabled={pending || !selectedView?.canManage} onClick={() => setConfirmDelete(true)}>Delete selected view</button></div></form>
    {confirmDelete && <div role="group" aria-label="Confirm saved view deletion"><p>Delete {selectedView?.name}? Saved defaults using this view will become unavailable.</p><button className="button danger" disabled={pending} onClick={() => void run(async signal => { await client.request({url:'/views/' + selected,method:'delete',signal,data:{expectedVersion:selectedView?.version}}); if(alive.current){setSelected('');setConfirmDelete(false);setRevision(r=>r+1);setMessage('View deleted.')} })}>Confirm delete view</button><button className="button subtle" disabled={pending} onClick={() => setConfirmDelete(false)}>Cancel deletion</button></div>}
    <p className="field-hint">Defaults apply when you enter this screen without URL filters. Reset stays reset for this visit. Save a list or board view to remember that layout. Sharing a personal link does not make it company-shared.</p><span className="sr-only">Company {workspaceId}</span>
  </div></details>
}
export function SavedViewPage({client}:{client:ApiClient}) {
  const {viewId=''}=useParams(), {result,retry}=useApiResource<SavedView>(client,'/views/'+encodeURIComponent(viewId))
  return <section className="projects-page"><h1>Saved view</h1>{result.status==='loading'?<p role="status">Checking view access...</p>:result.status==='error'?<div role="alert"><p>This saved view is unavailable or your access changed.</p><button className="button subtle" onClick={retry}>Retry saved view</button><Link to="/work">My work</Link></div>:<><h2>{result.data.name}</h2><p>{result.data.shared?'Company-shared':'Personal'} / {result.data.surface}</p><Link className="button primary" to={viewLink(result.data)}>Open this view</Link><p className="field-hint">Project and task access is checked again when this view opens.</p></>}</section>
}
