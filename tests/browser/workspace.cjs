// UI-only journeys: every /api call is intercepted. No real accounts or data are changed.
const { chromium } = require('playwright-core');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const origin = process.env.TASKFLOW_UI_ORIGIN || 'http://localhost:5173';
if (!['localhost', '127.0.0.1'].includes(new URL(origin).hostname)) throw Error('Use a local development origin for mocked UI review.');
const out = path.resolve(__dirname, '../../TestResults/workspace-ui'); fs.mkdirSync(out, {recursive:true});
const now = '2026-10-25T08:00:00Z';
const user = {id:'11',displayName:'Alex Morgan',email:'alex@example.test'};
const projects = [
  {id:'101',name:'Website redesign',description:'A thoughtful new home for our product. Align the team, refine the experience, and launch with confidence.'},
  {id:'102',name:'Product launch',description:'Bring the next release to market with a clear plan and shared ownership.'},
  {id:'103',name:'Customer experience',description:'Turn customer insights into improvements that make a difference.'},
  {id:'104',name:'Design system',description:'Consistent foundations for everything we build together.'}
].map(p=>({...p,ownerId:'11',createdAtUtc:now,updatedAtUtc:now}));
const members=[{userId:'11',displayName:'Alex Morgan',role:'Owner',joinedAtUtc:now},{userId:'12',displayName:'Jamie Chen',role:'Member',joinedAtUtc:now}];
let tasks=[
 {id:'201',title:'Review the homepage experience',status:'InProgress',priority:'High',assigneeId:'12',dueDateUtc:'2026-10-20T12:00:00Z'},
 {id:'202',title:'Finalize the visual direction',status:'Done',priority:'High',assigneeId:'11',dueDateUtc:now},
 {id:'203',title:'Prepare accessibility checklist',status:'ToDo',priority:'Medium',assigneeId:'12',dueDateUtc:'2026-11-02T12:00:00Z'},
 {id:'204',title:'Write product messaging',status:'ToDo',priority:'Low',assigneeId:null,dueDateUtc:null}
].map(t=>({...t,projectId:'101',description:'Keep the scope focused and share the outcome with the team.',createdAtUtc:now,updatedAtUtc:now}));
let events=[['TaskStatusChanged','Review the homepage experience moved to In Progress.','201'],['TaskAssigned','Jamie was assigned to the homepage review.','201'],['TaskCreated','Prepare accessibility checklist was created.','203'],['MemberAdded','Jamie joined the project.',null],['ProjectCreated','Website redesign was created.',null]].map(([actionType,details,taskId],i)=>({id:String(501+i),projectId:'101',actorUserId:'11',taskId,actionType,details,createdAtUtc:new Date(Date.parse(now)-i*3600000).toISOString()}));
let writes=[]; let authenticated=true;
function collection(items,query){ const page=Number(query.get('page')||1),pageSize=Number(query.get('pageSize')||20); return {items:items.slice((page-1)*pageSize,page*pageSize),page,pageSize,totalCount:items.length,totalPages:Math.ceil(items.length/pageSize)}; }
function event(actionType,details,taskId=null){events.unshift({id:String(700+events.length),projectId:'101',actorUserId:'11',taskId,actionType,details,createdAtUtc:new Date().toISOString()});}
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 const context=await browser.newContext({viewport:{width:1440,height:1050},acceptDownloads:true});
 const page=await context.newPage();const errors=[];page.on('pageerror',e=>errors.push(e.message));
 await context.route(origin+'/api/v1/**',async route=>{
  const request=route.request(),url=new URL(request.url()),p=url.pathname.replace('/api/v1',''),method=request.method(),q=url.searchParams;
  const body=request.postData()?JSON.parse(request.postData()):{};
  const send=(data,status=200)=>route.fulfill({status,contentType:'application/json',body:status===204?'':JSON.stringify(data)});
  if(method!=='GET')writes.push({p,method});
  if(p==='/auth/me')return send(user,authenticated?200:401);
  if(p==='/users/me'&&method==='PATCH'){user.displayName=body.displayName;return send(user);}
  if(p==='/auth/csrf')return send({requestToken:'ui-only-token',headerName:'X-CSRF-TOKEN'});
  if(p==='/dashboard')return send({totalProjects:projects.length,totalTasks:tasks.length,tasksByStatus:{toDo:tasks.filter(t=>t.status==='ToDo').length,inProgress:tasks.filter(t=>t.status==='InProgress').length,done:tasks.filter(t=>t.status==='Done').length},assignedToMe:2,overdueTasks:1});
  if(p==='/projects'){
   if(method==='POST'){const item={...body,id:String(900+projects.length),ownerId:'11',createdAtUtc:now,updatedAtUtc:now};projects.push(item);return send(item,201);}
   return send(collection(projects.filter(x=>!q.get('search')||x.name.toLowerCase().includes(q.get('search').toLowerCase())),q));
  }
  const pm=p.match(/^\/projects\/([^/]+)(?:\/(members|tasks|activity))?$/);
  if(pm){const project=projects.find(x=>x.id===pm[1]);if(!project)return send({status:404,title:'Not found'},404);
   if(pm[2]==='members')return send({items:members});
   if(pm[2]==='activity')return send(collection(events.filter(e=>e.projectId===pm[1]&&(!q.get('fromUtc')||e.createdAtUtc>=q.get('fromUtc'))&&(!q.get('toUtc')||e.createdAtUtc<=q.get('toUtc'))),q));
   if(pm[2]==='tasks'){
    if(method==='POST'){const task={...body,id:String(800+tasks.length),projectId:pm[1],status:'ToDo',createdAtUtc:now,updatedAtUtc:now};tasks.push(task);event('TaskCreated',task.title+' was created.',task.id);return send(task,201);}
    let rows=tasks.filter(t=>t.projectId===pm[1]);for(const k of ['status','priority','assigneeId'])if(q.get(k))rows=rows.filter(t=>t[k]===q.get(k));if(q.get('search'))rows=rows.filter(t=>t.title.toLowerCase().includes(q.get('search').toLowerCase()));return send(collection(rows,q));
   }
   if(method==='PUT')Object.assign(project,body);
   if(method==='DELETE'){projects.splice(projects.indexOf(project),1);return send(null,204);}
   const rows=tasks.filter(t=>t.projectId===project.id),done=rows.filter(t=>t.status==='Done').length;return send({...project,totalTasks:rows.length,completedTasks:done,progressPercentage:rows.length?Math.round(done/rows.length*100):0});
  }
  const tm=p.match(/^\/tasks\/([^/]+)(?:\/(status))?$/);
  if(tm){const task=tasks.find(t=>t.id===tm[1]);if(!task)return send({status:404,title:'Not found'},404);
   if(method==='DELETE'){tasks=tasks.filter(t=>t.id!==task.id);event('TaskDeleted',task.title+' was deleted.',task.id);return send(null,204);}
   if(method==='PATCH'){Object.assign(task,body);event(tm[2]?'TaskStatusChanged':'TaskUpdated',task.title+' was updated.',task.id);}
   return send(task);
  }
  return send({status:404,title:'Not found'},404);
 });
 const checks=[];
 async function review(name,url,width=1440){
  await page.setViewportSize({width,height:1000});await page.goto(origin+url);await page.getByRole('navigation',{name:'Main navigation'}).waitFor();await page.waitForLoadState('networkidle');
  await page.addScriptTag({path:require.resolve('axe-core/axe.min.js')});
  const result=await page.evaluate(async()=>({axe:await axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21aa']}}),overflow:document.documentElement.scrollWidth>innerWidth}));
  checks.push({name,width,violations:result.axe.violations.map(v=>({id:v.id,impact:v.impact,nodes:v.nodes.map(n=>n.target)})),overflow:result.overflow});
  await page.screenshot({path:path.join(out,name+'.png'),fullPage:true});
 }
 await review('overview-desktop','/dashboard');await review('projects-desktop','/projects');await review('tasks-desktop','/projects/101/tasks');await review('project-desktop','/projects/101');await review('activity-desktop','/projects/101/activity');await review('activity-center','/activity');await review('profile-desktop','/profile');await review('create-project-desktop','/projects/new');await review('edit-project-desktop','/projects/101/edit');await review('create-task-desktop','/projects/101/tasks/new');await review('task-detail-desktop','/tasks/201');await review('edit-task-desktop','/tasks/201/edit');
 await page.goto(origin+'/profile');await page.getByLabel('Display name').fill('Alex Taylor');await page.getByRole('button',{name:'Save profile'}).click();await page.getByRole('status').filter({hasText:'Profile saved.'}).waitFor();assert.equal(user.displayName,'Alex Taylor');
 const download=page.waitForEvent('download');await page.goto(origin+'/projects/101/activity');await page.getByRole('button',{name:'Export this page'}).click();const file=await download;const exported=JSON.parse(fs.readFileSync(await file.path(),'utf8'));assert.equal(exported.projectId,'101');assert.equal(exported.scope,'Current filtered page only');assert.equal(typeof exported.events[0].id,'string');
 await page.goto(origin+'/projects/101/tasks');await page.getByLabel('Status for Review the homepage experience').selectOption('Done');await page.getByLabel('Status for Review the homepage experience').waitFor();await page.waitForTimeout(200);assert.equal(tasks.find(t=>t.id==='201').status,'Done');
 await page.getByRole('link',{name:'Edit Review the homepage experience',exact:true}).click();await page.getByLabel('Task title').fill('Homepage review completed');await page.getByRole('button',{name:'Save changes'}).click();await page.getByRole('heading',{name:'Homepage review completed',exact:true}).waitFor();
 await page.getByRole('button',{name:'Delete task',exact:true}).click();assert.equal(await page.getByRole('button',{name:'Keep task'}).evaluate(el=>el===document.activeElement),true);await page.keyboard.press('Escape');await page.waitForFunction(()=>document.activeElement?.textContent==='Delete task');assert(tasks.some(t=>t.id==='201'));await page.getByRole('button',{name:'Delete task',exact:true}).click();await page.getByRole('button',{name:'Confirm task deletion'}).click();await page.getByRole('status').filter({hasText:'Task deleted.'}).waitFor();assert(!tasks.some(t=>t.id==='201'));
 await page.goto(origin+'/projects/new');await page.getByLabel('Project name').fill('Browser journey project');await page.getByRole('button',{name:'Create project',exact:true}).click();await page.getByRole('heading',{name:'Browser journey project',exact:true}).waitFor();await page.getByRole('link',{name:'Edit project',exact:true}).click();await page.getByLabel('Project name').fill('Updated browser journey');await page.getByRole('button',{name:'Save changes',exact:true}).click();await page.getByRole('heading',{name:'Updated browser journey',exact:true}).waitFor();await page.getByRole('button',{name:'Delete project',exact:true}).click();await page.getByRole('button',{name:'Confirm deletion',exact:true}).click();await page.getByRole('heading',{name:'Projects',exact:true}).waitFor();assert(!projects.some(p=>p.name==='Updated browser journey'));
 for(const width of [390,320])for(const [name,url] of [['overview','/dashboard'],['projects','/projects'],['tasks','/projects/101/tasks'],['activity','/projects/101/activity'],['profile','/profile'],['new-project','/projects/new'],['new-task','/projects/101/tasks/new'],['project-detail','/projects/101']])await review(name+'-'+width,url,width);
 user.id='12';await page.goto(origin+'/projects/101');await page.getByRole('heading',{name:'Website redesign',exact:true}).waitFor();assert.equal(await page.getByRole('button',{name:'Delete project',exact:true}).count(),0);assert.equal(await page.getByRole('link',{name:'Edit project',exact:true}).count(),0);await page.goto(origin+'/tasks/202');await page.getByRole('link',{name:'Edit task',exact:true}).waitFor();assert.equal(await page.getByRole('button',{name:'Delete task',exact:true}).count(),0);user.id='11';
 await page.setViewportSize({width:390,height:850});await page.goto(origin+'/projects/101/tasks');const table=page.getByRole('region',{name:'Project task table, scroll horizontally for more columns'});await table.waitFor();await table.focus();await page.keyboard.press('ArrowRight');await page.waitForFunction(()=>document.querySelector('.task-table-wrap').scrollLeft>0);
 await review('overview-tablet','/dashboard',768);await review('project-tablet','/projects/101',768);
 authenticated=false;
 for(const width of [1440,390,320]){await review('home-'+width,'/',width);await review('not-found-'+width,'/page-not-found',width);await review('login-'+width,'/login',width);await page.getByLabel('Password',{exact:true}).fill('Visibility-Only-123!');await page.getByRole('button',{name:'Show password'}).click();assert.equal(await page.getByLabel('Password',{exact:true}).getAttribute('type'),'text');await page.getByRole('button',{name:'Hide password'}).click();await review('register-'+width,'/register',width);await page.getByRole('button',{name:'Create account',exact:true}).click();assert.equal(await page.locator('#displayName').evaluate(el=>el===document.activeElement),true);}
 assert.equal(errors.length,0,'Browser errors: '+errors.join('; '));
 const report={environment:'Local Vite with intercepted synthetic API fixtures; no production data touched',checks,writes,journeys:['inline status','edit task','cancel delete','confirmed delete task','project create/edit/delete','current-page audit JSON export','profile edit','password visibility','form validation focus','member permission controls','keyboard confirmation cancel','keyboard table scrolling'],browserErrors:errors};fs.writeFileSync(path.join(out,'review.json'),JSON.stringify(report,null,2));
 console.log(JSON.stringify({scans:checks.length,violations:checks.reduce((n,c)=>n+c.violations.length,0),overflow:checks.filter(c=>c.overflow).length,journeys:report.journeys}));
 await browser.close();
 if(checks.some(c=>c.overflow||c.violations.length))process.exitCode=1;
})().catch(e=>{console.error(e.message);process.exit(1)});
