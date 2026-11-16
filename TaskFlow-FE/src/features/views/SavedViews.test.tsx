import { viewLink } from './view-link'
import { AuthProvider } from '../auth/AuthProvider'
import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'
import { CompanyContext } from '../companies/company-context'
import { SavedViews, SavedViewPage } from './SavedViews'
import type { SavedView } from './SavedViews'
const saved = (id = '10'): SavedView => ({ id, userId: '1', name: 'Overdue work', surface: 'work', projectId: null, shared: false, filters: { queue: 'overdue', assignment: 'mine' }, version: '3', canManage: true })
function Location() { const location = useLocation(), navigate = useNavigate(); return <><output aria-label="Current route">{location.pathname + location.search}</output><button onClick={() => void navigate('/companies/1/work')}>Reset route</button></> }
function setup(options: { initial?: string; defaultId?: string; role?: string; manage?: boolean; writeStatus?: number; readStatus?: number } = {}) {
 const auth = server(true), state = { views: [ { ...saved(), canManage: options.manage ?? true } ], writes: [] as { url: string; data: Record<string, unknown> }[], preference: { context: 'work', savedViewId: options.defaultId ?? null, version: '1', available: true } }
 const root = createApiClient(async c => {
  if(c.url?.startsWith('/auth/')) return auth.adapter(c)
  const u = new URL(c.url!, 'https://example.test')
  if(c.method !== 'get') {
   const data = JSON.parse(c.data); state.writes.push({url:u.pathname,data})
   if(options.writeStatus) return reply(c,options.writeStatus,{})
   if(u.pathname.startsWith('/view-preferences/')) { state.preference = { ...state.preference, savedViewId:data.savedViewId,version:'2' }; return reply(c,200,state.preference) }
   if(c.method === 'delete'){state.views=[];return reply(c,204,null)}
   const v={...saved(),...data,id:'11',version:'1'};state.views.push(v);return reply(c,200,v)
  }
  if(u.pathname.startsWith('/view-preferences/')) return reply(c,200,state.preference)
  if(u.pathname === '/views') return reply(c,200,{items:state.views,page:1,totalPages:1,totalCount:state.views.length,pageSize:20})
  if(u.pathname.startsWith('/views/')) return reply(c,options.readStatus ?? 200,state.views.find(v=>v.id===u.pathname.split('/').at(-1)) ?? {})
  return reply(c,404,{})
 })
 const client=root.forWorkspace('1')
 render(<MemoryRouter initialEntries={[options.initial ?? '/companies/1/work']}><AuthProvider client={root}><CompanyContext.Provider value={{role:options.role ?? 'Owner',lifecycleEnabled:true}}><Routes><Route path="/companies/:workspaceId/work" element={<><Location /><SavedViews client={client} surface="work" /></>} /><Route path="/companies/:workspaceId/views/:viewId" element={<SavedViewPage client={client} />} /></Routes></CompanyContext.Provider></AuthProvider></MemoryRouter>)
 return state
}
async function open() { await userEvent.click(screen.getByText('Saved views & preferences')); await screen.findByRole('option',{name:'Overdue work (Personal / work)'}) }
async function choose() { await open(); await userEvent.selectOptions(screen.getByLabelText('Saved view'),'10') }
describe('saved views and preferences',()=>{
 it('applies a persisted default on an unfiltered entry and keeps Reset reset',async()=>{setup({defaultId:'10'});await waitFor(()=>expect(screen.getByLabelText('Current route').textContent).toContain('queue=overdue'));await userEvent.click(screen.getByRole('button',{name:'Reset route'}));expect(screen.getByLabelText('Current route')).toHaveTextContent('/companies/1/work');expect(screen.getByLabelText('Current route').textContent).not.toContain('?')})
 it('preserves explicit shared URL filters over the default',async()=>{setup({defaultId:'10',initial:'/companies/1/work?queue=due'});await open();expect(screen.getByLabelText('Current route')).toHaveTextContent('queue=due')})
 it('saves the current filters without a transient page and scopes the share link',async()=>{const state=setup({initial:'/companies/1/work?queue=due&page=8'});await open();await userEvent.type(screen.getByLabelText('View name'),'Deadline view');await userEvent.click(screen.getByRole('button',{name:'Save current view as new'}));await screen.findByText('View saved.');expect(state.writes[0].data.filters).toEqual({queue:'due'});expect(state.writes[0].data.shared).toBe(false);expect(screen.getByRole('link',{name:'Share link'})).toHaveAttribute('href','/companies/1/views/11')})
 it('keeps a conflict draft and never silently replays a write',async()=>{const state=setup({writeStatus:409});await choose();await userEvent.clear(screen.getByLabelText('View name'));await userEvent.type(screen.getByLabelText('View name'),'My draft');await userEvent.click(screen.getByRole('button',{name:'Replace selected with current view'}));expect(await screen.findByRole('alert')).toHaveTextContent('This view changed');expect(state.writes).toHaveLength(1);expect(state.writes[0].data.expectedVersion).toBe('3');expect(screen.getByLabelText('View name')).toHaveValue('My draft')})
 it('requires deletion confirmation and sends the captured revision',async()=>{const state=setup();await choose();await userEvent.click(screen.getByRole('button',{name:'Delete selected view'}));expect(state.writes).toHaveLength(0);await userEvent.click(screen.getByRole('button',{name:'Cancel deletion'}));expect(state.writes).toHaveLength(0);await userEvent.click(screen.getByRole('button',{name:'Delete selected view'}));await userEvent.click(screen.getByRole('button',{name:'Confirm delete view'}));await screen.findByText('View deleted.');expect(state.writes[0].data.expectedVersion).toBe('3')})
 it('keeps company sharing and shared management unavailable to a member',async()=>{setup({role:'Member',manage:false});await choose();expect(screen.queryByLabelText('Share with company')).not.toBeInTheDocument();expect(screen.getByRole('button',{name:'Delete selected view'})).toBeDisabled()})
 it('persists a self default with a captured preference version',async()=>{const state=setup();await choose();await userEvent.click(screen.getByRole('button',{name:'Use as default'}));await screen.findByText('Default saved for this screen.');expect(state.writes[0]).toEqual({url:'/view-preferences/work',data:{savedViewId:'10',expectedVersion:'1'}})})
 it('shows no open link for an inaccessible personal share',async()=>{setup({initial:'/companies/1/views/10',readStatus:404});expect(await screen.findByRole('alert')).toHaveTextContent('unavailable');expect(screen.queryByRole('link',{name:'Open this view'})).not.toBeInTheDocument()})
 it('constructs project/list/board links without dropping filter context',()=>{expect(viewLink({...saved(),surface:'tasks',projectId:'41',filters:{priority:'High'}})).toBe('/projects/41/tasks?priority=High');expect(viewLink({...saved(),surface:'board',projectId:'41',filters:{}})).toBe('/projects/41/board');expect(viewLink({...saved(),projectId:'41',filters:{queue:'all'}})).toBe('/work?queue=all&projectId=41')})
})
