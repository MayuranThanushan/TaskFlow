// Loopback-only production UI review. All API requests are synthetic.
const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path');
const { chromium } = require('playwright-core');
const origin = process.env.TASKFLOW_WORK_UI_ORIGIN;
if (!origin || new URL(origin).origin !== origin || new URL(origin).hostname !== '127.0.0.1' || process.env.TASKFLOW_WORK_UI_MOCKS !== '1') throw Error('Explicit loopback origin and TASKFLOW_WORK_UI_MOCKS=1 required');
const output = path.resolve(__dirname, '../../TestResults/saas033-browser'); fs.mkdirSync(output, { recursive: true });
(async () => {
 const browser = await chromium.launch({headless:true, channel:'msedge'}), report = {scope:'mocked API production frontend; no real account/database', accessibility:[], errors:[], checks:[]};
 try {
 const context = await browser.newContext(), page = await context.newPage(); let status = 200, empty = false; const calls = [];
 page.on('pageerror', () => report.errors.push('Browser script error'));
 await context.route(origin + '/__work_axe.js', r => r.fulfill({contentType:'application/javascript',body:fs.readFileSync(require.resolve('axe-core/axe.min.js'),'utf8')}));
 await context.route('**/api/**', r => {
 const u=new URL(r.request().url()), json=(data,code=200)=>r.fulfill({status:code,contentType:'application/json',body:JSON.stringify(data)});
 if(u.pathname.endsWith('/auth/me')) return json({id:'1',displayName:'Alex Morgan',email:'synthetic@example.test'});
 if(u.pathname.endsWith('/auth/csrf')) return json({requestToken:'synthetic-token'});
 if(u.pathname==='/api/v2/workspaces/1') return json({id:'1',name:'Product studio',role:'Member'});
 if(u.pathname==='/api/v2/workspaces/1/work') {calls.push(u.search);return json({items:empty?[]:[{task:{id:'101',projectId:'41',title:'Review the product launch and customer onboarding checklist',status:'ToDo',priority:'High',assigneeId:'1',dueDateUtc:'2026-11-15T08:00:00Z',createdAtUtc:'2026-11-15T08:00:00Z',updatedAtUtc:'2026-11-15T08:00:00Z',version:'1'},projectName:'Quarterly product launch',assigneeName:'Alex Morgan'}],page:1,pageSize:20,totalCount:empty?0:1,totalPages:empty?0:1,counts:{open:1,due:0,overdue:1,total:1,completed:0},asOfUtc:'2026-11-16T08:00:00Z',dueThroughUtc:'2026-11-23T08:00:00Z'},status);}
 return json({},404);
 });
 async function scan(name){for(const width of [1440,320]){await page.setViewportSize({width,height:1050});await page.addScriptTag({url:origin+'/__work_axe.js'});const result=await page.evaluate(async()=>({violations:(await axe.run(document,{runOnly:{type:'tag',values:['wcag2a','wcag2aa','wcag21aa','wcag22aa','best-practice']}})).violations.map(v=>({id:v.id,targets:v.nodes.map(n=>n.target)})),overflow:document.documentElement.scrollWidth>innerWidth}));report.accessibility.push({name,width,...result});assert.deepEqual(result.violations,[]);assert.equal(result.overflow,false);await page.screenshot({path:path.join(output,name+'-'+width+'.png'),fullPage:true});}}
 await page.goto(origin+'/companies/1/work');await page.getByRole('link',{name:'Review the product launch and customer onboarding checklist',exact:true}).waitFor();await scan('work-default');
 assert.equal(calls.length,1);report.checks.push('single cross-project summary read');
 await page.getByRole('button',{name:/Due soon/}).click();await page.waitForURL('**queue=due');await page.getByRole('button',{name:'Refresh work',exact:true}).waitFor();assert.ok(calls.at(-1).includes('queue=due'));await scan('work-due');
 await page.getByLabel('Show work',{exact:true}).selectOption('unassigned');await page.getByLabel('Search tasks or projects').fill('launch');await page.getByRole('button',{name:'Apply filters',exact:true}).click();await page.waitForURL('**search=launch*');report.checks.push('queue and assignment/search URL navigation');
 empty=true;await page.reload();await page.getByRole('heading',{name:'No work matches this view.',exact:true}).waitFor();await scan('work-empty');
 status=400;await page.reload();await page.getByRole('button',{name:'Reset invalid filters',exact:true}).waitFor();await scan('work-invalid');
 status=404;await page.reload();await page.getByRole('heading',{name:'Company or project not available.',exact:true}).waitFor();await scan('work-access-lost');assert.equal(await page.getByRole('link',{name:'Review the product launch and customer onboarding checklist',exact:true}).count(),0);
 assert.deepEqual(report.errors,[]);report.passed=true;fs.writeFileSync(path.join(output,'work-ui.json'),JSON.stringify(report,null,2));console.log(JSON.stringify({scans:report.accessibility.length,violations:0,overflow:false,checks:report.checks.length}));
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1});
