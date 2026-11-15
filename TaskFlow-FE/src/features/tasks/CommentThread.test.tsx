import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useMemo, useState } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { describe, expect, it } from 'vitest'
import { AuthProvider } from '../auth/AuthProvider'
import { createApiClient } from '../../api/client'
import { reply, server } from '../../test/server'
import { CommentThread } from './CommentThread'
import type { components } from '../../api/generated'
type Comment = components['schemas']['TaskCommentDto']
const initial = (): Comment => ({ id:'9007199254741201',taskId:'55',authorId:'1',authorName:'Alex',body:'<script>private text</script> @[9007199254741999]',version:'1',createdAtUtc:'2026-11-15T08:00:00Z',editedAtUtc:null,deletedAtUtc:null,canEdit:true,canDelete:true,mentions:[{userId:'9007199254741999',displayName:'Robin'}] })
function setup() {
  const auth=server(true), state={items:[initial()],canComment:true,status:200,code:'',searches:[] as string[],writes:[] as {url:string;method:string;data:Record<string,unknown>}[]}
  const client=createApiClient(async c=>{
    if(c.baseURL==='/api/v1')return auth.adapter(c)
    if(c.url?.includes('mention-candidates')){state.searches.push(c.url);return reply(c,200,{items:[{userId:'9007199254741999',displayName:'Robin'}]})}
    if(c.url?.endsWith('/history'))return reply(c,200,{items:[{version:'1',action:'Created',actorName:'Alex',createdAtUtc:'2026-11-15T08:00:00Z'}]})
    if(c.method==='get')return reply(c,200,{items:c.baseURL?.endsWith('/2')?[]:state.items,page:1,pageSize:20,totalCount:state.items.length,totalPages:1,canComment:state.canComment})
    const data=JSON.parse(c.data as string) as Record<string,unknown>;state.writes.push({url:c.url!,method:c.method!,data})
    if(state.status!==200)return reply(c,state.status,{code:state.code})
    if(c.method==='post')state.items=[{...initial(),id:'9007199254741202',body:data.body as string},...state.items]
    if(c.method==='put')state.items=state.items.map(row=>({...row,body:data.body as string,version:'2',editedAtUtc:'2026-11-15T09:00:00Z'}))
    if(c.method==='delete')state.items=state.items.map(row=>({...row,body:null,version:'2',deletedAtUtc:'2026-11-15T09:00:00Z',canEdit:false,canDelete:false,mentions:[]}))
    return reply(c,c.method==='delete'?204:200,state.items[0])
  })
  function Harness(){const [workspace,setWorkspace]=useState('1');const scoped=useMemo(()=>client.forWorkspace(workspace),[workspace]);return <><CommentThread key={workspace} client={scoped} taskId="55"/><button onClick={()=>setWorkspace('2')}>Switch company</button></>}
  render(<MemoryRouter><AuthProvider client={client}><Harness/></AuthProvider></MemoryRouter>);return {state,user:userEvent.setup()}
}
describe('task discussion',()=>{
  it('escapes raw text, resolves explicit mentions and searches only on user action',async()=>{
    const {state,user}=setup();expect(await screen.findByText(/<script>private text<\/script>/)).toBeVisible();expect(document.querySelector('.comment-body script')).toBeNull();expect(screen.getByText('@Robin')).toBeVisible();expect(state.searches).toHaveLength(0)
    await user.type(screen.getByLabelText('Find a project collaborator'),'Rob');await user.click(screen.getByRole('button',{name:'Find people'}));await user.click(await screen.findByRole('button',{name:'Mention Robin'}))
    expect(screen.getByLabelText('Add a comment')).toHaveValue('@[9007199254741999] ')
    await user.click(screen.getByRole('button',{name:'Post comment'}));await waitFor(()=>expect(state.writes).toHaveLength(1));expect(state.writes[0].data).toEqual({body:'@[9007199254741999] '})
  })
  it('keeps an edit draft on conflict and never retries a write automatically',async()=>{
    const {state,user}=setup();await screen.findByText('@Robin');await user.click(screen.getByRole('button',{name:'Edit comment'}));const form=screen.getByRole('form',{name:'Edit comment'});await user.clear(within(form).getByLabelText('Edit your comment'));await user.type(within(form).getByLabelText('Edit your comment'),'My changed draft');state.status=409;state.code='resource.conflict'
    await user.click(within(form).getByRole('button',{name:'Save comment'}));expect(await screen.findByRole('alert')).toHaveTextContent('Your draft is kept');expect(within(form).getByLabelText('Edit your comment')).toHaveValue('My changed draft');expect(state.writes).toHaveLength(1);expect(state.writes[0].data.expectedVersion).toBe('1')
  })
  it('confirms deletion, clears text and offers metadata-only history',async()=>{
    const {state,user}=setup();await screen.findByText('@Robin');await user.click(screen.getByRole('button',{name:'Delete comment'}));expect(state.writes).toHaveLength(0);await user.dblClick(screen.getByRole('button',{name:'Confirm comment deletion'}));expect(await screen.findByText('Comment deleted.')).toBeVisible();expect(state.writes).toHaveLength(1);expect(state.writes[0].data).toEqual({expectedVersion:'1'})
    await user.click(screen.getByRole('button',{name:'Revision history'}));expect(await screen.findByText(/Created by Alex/)).toBeVisible();expect(screen.getByText(/Previous text is not retained/)).toBeVisible()
  })
  it('hides private comments after a revoked write',async()=>{
    const {state,user}=setup();await screen.findByText('@Robin');state.status=404;await user.type(screen.getByLabelText('Add a comment'),'Private draft');await user.click(screen.getByRole('button',{name:'Post comment'}));expect(await screen.findByRole('heading',{name:'Comments unavailable'})).toBeVisible();expect(screen.queryByText('@Robin')).not.toBeInTheDocument();expect(screen.queryByLabelText('Add a comment')).not.toBeInTheDocument()
  })
  it('clears the draft and prior company rows when switching companies',async()=>{
    const {user}=setup();await screen.findByText('@Robin');await user.type(screen.getByLabelText('Add a comment'),'Old company draft');await user.click(screen.getByRole('button',{name:'Switch company'}));await screen.findByText('No comments on this page.');expect(screen.getByLabelText('Add a comment')).toHaveValue('');expect(screen.queryByText('@Robin')).not.toBeInTheDocument()
  })
})
