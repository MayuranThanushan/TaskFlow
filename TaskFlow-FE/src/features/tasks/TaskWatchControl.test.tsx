import { render,screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useMemo,useState } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { describe,expect,it } from 'vitest'
import { AuthProvider } from '../auth/AuthProvider'
import { createApiClient } from '../../api/client'
import { reply,server } from '../../test/server'
import { TaskWatchControl } from './TaskWatchControl'
function setup(){
  const auth=server(true),state={value:{isWatching:false,version:'0'},status:200,writes:[] as {data:Record<string,unknown>;base:string}[]}
  const client=createApiClient(async c=>{
    if(c.baseURL==='/api/v1')return auth.adapter(c)
    if(c.method==='get')return reply(c,200,c.baseURL?.endsWith('/2')?{isWatching:false,version:'0'}:state.value)
    const data=JSON.parse(c.data as string) as Record<string,unknown>;state.writes.push({data,base:c.baseURL!})
    if(state.status!==200)return reply(c,state.status,{code:state.status===409?'resource.conflict':''})
    state.value={isWatching:data.isWatching as boolean,version:String(Number(state.value.version)+1)};return reply(c,200,state.value)
  })
  function Harness(){const [workspace,setWorkspace]=useState('1');const scoped=useMemo(()=>client.forWorkspace(workspace),[workspace]);return <><TaskWatchControl key={workspace} client={scoped} taskId="9007199254741999"/><button onClick={()=>setWorkspace('2')}>Switch company</button></>}
  render(<MemoryRouter><AuthProvider client={client}><Harness/></AuthProvider></MemoryRouter>);return {state,user:userEvent.setup()}
}
describe('personal task watcher',()=>{
  it('subscribes once through a fast double click and can stop after refresh',async()=>{
    const {state,user}=setup();await user.dblClick(await screen.findByRole('button',{name:'Watch task'}));expect(await screen.findByRole('button',{name:'Stop watching task'})).toHaveAttribute('aria-pressed','true');expect(state.writes).toHaveLength(1);expect(state.writes[0]).toEqual({base:'/api/v2/workspaces/1',data:{isWatching:true,expectedVersion:'0'}})
    await user.click(screen.getByRole('button',{name:'Stop watching task'}));await screen.findByRole('button',{name:'Watch task'});expect(state.writes[1].data).toEqual({isWatching:false,expectedVersion:'1'})
  })
  it('requires explicit conflict reload and never automatically retries a write',async()=>{
    const {state,user}=setup();await screen.findByRole('button',{name:'Watch task'});state.status=409;state.value={isWatching:true,version:'1'};await user.click(screen.getByRole('button',{name:'Watch task'}));expect(await screen.findByRole('alert')).toHaveTextContent('Reload before trying again');expect(state.writes).toHaveLength(1)
    await user.click(screen.getByRole('button',{name:'Reload subscription'}));await screen.findByRole('button',{name:'Stop watching task'});expect(state.writes).toHaveLength(1)
  })
  it('hides subscription controls after revocation and clears state on company switch',async()=>{
    const {state,user}=setup();state.status=404;await user.click(await screen.findByRole('button',{name:'Watch task'}));expect(await screen.findByText('Task watching is unavailable.')).toBeVisible();expect(screen.queryByRole('button',{name:'Watch task'})).not.toBeInTheDocument()
    await user.click(screen.getByRole('button',{name:'Switch company'}));expect(await screen.findByRole('button',{name:'Watch task'})).toHaveAttribute('aria-pressed','false')
  })
})
