import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import { asApiError } from '../../api/client'
import type { components } from '../../api/generated'
import { useApiResource } from '../../hooks/use-api-resource'
import { RetryError } from '../../components/Feedback'
type Watcher=components['schemas']['TaskWatcherDto']
export function TaskWatchControl({client,taskId}:{client:ApiClient;taskId:string}){
  const path='/tasks/'+taskId+'/watcher';const {result,retry}=useApiResource<Watcher>(client,path)
  const [unavailable,setUnavailable]=useState(false),[notice,setNotice]=useState('')
  if(unavailable||result.status==='error'&&[403,404].includes(result.error.status??0))return <p className="field-hint">Task watching is unavailable.</p>
  return <section className="task-watching" aria-label="Task watching"><h2>Follow this task</h2><p className="muted">Receive future task changes and new comments in your inbox. Watching does not change task access.</p>
    {result.status==='loading'?<p role="status">Loading your subscription...</p>:result.status==='error'?<RetryError message={result.error.message} retry={retry}/>:<WatchButton key={result.data.version} client={client} path={path} value={result.data} reload={retry} onDenied={()=>setUnavailable(true)} onSaved={next=>{setNotice(next.isWatching?'You are watching this task.':'You stopped watching this task.');retry()}}/>}
    {notice&&<p role="status">{notice}</p>}<p className="field-hint">Quiet hours and digest delivery are managed in Inbox preferences. Stopping affects future updates; existing inbox items remain.</p>
  </section>
}
function WatchButton({client,path,value,reload,onSaved,onDenied}:{client:ApiClient;path:string;value:Watcher;reload:()=>void;onSaved:(value:Watcher)=>void;onDenied:()=>void}){
  const [error,setError]=useState(''),[pending,setPending]=useState(false)
  const lock=useRef(false),abort=useRef<AbortController|null>(null)
  useEffect(()=>{const c=new AbortController();abort.current=c;return()=>c.abort()},[])
  async function change(){
    if(lock.current)return;lock.current=true;setPending(true);setError('');let committed=false
    try{const next=await client.request<Watcher>({url:path,method:'put',data:{isWatching:!value.isWatching,expectedVersion:value.version},signal:abort.current?.signal});committed=true;if(!abort.current?.signal.aborted)onSaved(next)}
    catch(failure){if(!abort.current?.signal.aborted){const e=asApiError(failure);setError(e.status===409?'Your subscription changed in another tab. Reload before trying again.':Object.values(e.fields).flat().join(' ')||e.message);if([403,404].includes(e.status??0)&&e.code!=='mfa.administration_required')onDenied()}}
    finally{if(!committed){lock.current=false;if(!abort.current?.signal.aborted)setPending(false)}}
  }
  return <><button className="button subtle" disabled={pending} aria-pressed={value.isWatching} onClick={()=>void change()}>{pending?'Updating subscription...':value.isWatching?'Stop watching task':'Watch task'}</button>
    {error&&<p role="alert" className="error-summary">{error} <button className="button subtle" disabled={pending} onClick={reload}>Reload subscription</button> <Link to="/account-security">Account security</Link></p>}</>
}
