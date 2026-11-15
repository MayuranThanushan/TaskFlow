import { Link, useParams } from 'react-router-dom'
import type { ApiClient } from '../../api/client'
import type { components } from '../../api/generated'
import { useApiResource } from '../../hooks/use-api-resource'
import { Pagination, RetryError } from '../../components/Feedback'
type Groups=components['schemas']['NotificationGroupPage']
export function GroupedInbox({client,page,onPage,onView,onTask,onChanged}:{client:ApiClient;page:number;onPage:(page:number)=>void;onView:(view:string)=>void;onTask:(taskId:string)=>void;onChanged:()=>void}){
  const {workspaceId}=useParams();const {result,retry}=useApiResource<Groups>(client,`/notifications/groups?page=${page}&pageSize=20`)
  if(result.status==='error'&&[403,404].includes(result.error.status??0))return <section className="projects-page"><h1>Inbox unavailable</h1><p>Your access or notification settings may have changed.</p><Link to="/companies">Choose a company</Link></section>
  return <section className="projects-page inbox-page"><div className="page-heading"><div><p className="eyebrow">Your company updates</p><h1>Inbox</h1><p className="muted">One group per task. Open a group to review and mark its individual updates.</p></div><button className="button subtle" onClick={()=>{retry();onChanged()}}>Refresh inbox</button></div>
    <div className="inbox-filters" role="group" aria-label="Inbox filters"><button className="button subtle" onClick={()=>onView('all')}>All notifications</button><button className="button subtle" onClick={()=>onView('unread')}>Unread</button><button className="button subtle" aria-pressed="true">By task</button></div>
    {result.status==='loading'?<p role="status">Loading task groups...</p>:result.status==='error'?<RetryError message={result.error.message} retry={retry}/>:<>
      <p role="status">{result.data.unreadCount} unread notifications across your tasks</p>
      {!result.data.items.length?<div className="empty-state"><h2>No task updates on this page</h2>{page>1&&<button className="button subtle" onClick={()=>onPage(1)}>Return to first page</button>}</div>:<ol className="notification-list">{result.data.items.map(row=><li key={row.taskId} className={row.unreadCount?'is-unread':'is-read'}><div className="notification-copy"><h2><Link to={'/companies/'+workspaceId+'/tasks/'+row.taskId}>{row.taskTitle}</Link></h2><p>{row.projectName}</p><p>{row.notificationCount} updates &middot; {row.unreadCount} unread</p><time dateTime={row.latestAtUtc}>{new Date(row.latestAtUtc).toLocaleString()}</time></div><button className="button subtle" aria-label={'View updates for '+row.taskTitle} onClick={()=>onTask(row.taskId)}>View updates</button></li>)}</ol>}
      <Pagination label="Inbox task groups" page={page} totalPages={result.data.totalPages} onPage={onPage}/>
    </>}
  </section>
}
