const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const scenario = JSON.parse(fs.readFileSync(process.env.TASKFLOW_COMMENT_SCENARIO, 'utf8'));
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] }; let stage = 'start';
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1050 } }), page = await context.newPage();
    page.on('pageerror', () => report.errors.push('Browser script error; private details withheld'));
    await context.route(origin + '/__comment_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    async function scan(name) {
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1050 }); await page.addScriptTag({ url: origin + '/__comment_axe.js' });
        const result = await page.evaluate(async () => { const audit = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a','wcag2aa','wcag21aa','wcag22aa','best-practice'] } }); return { violations: audit.violations.map(v => ({ id:v.id, targets:v.nodes.map(n => n.target) })), overflow:document.documentElement.scrollWidth > innerWidth }; });
        report.accessibility.push({ name,width,...result }); assert.equal(result.violations.length,0); assert.equal(result.overflow,false);
        await page.screenshot({path:artifact('comments-'+name+'-'+width+'.png'),fullPage:true});
      }
      await page.setViewportSize({width:1440,height:1050});
    }
    async function login(email) { await page.getByLabel('Email address').fill(email); await page.getByLabel('Password',{exact:true}).fill(scenario.password); await submitLogin(page); }
    const task='/companies/'+scenario.workspaceId+'/tasks/'+scenario.taskId;
    stage='owner-create';await page.goto(origin+task);await login(scenario.ownerEmail);await page.getByRole('heading',{name:'Comments',exact:true}).waitFor();
    await page.getByLabel('Add a comment',{exact:true}).fill('Release decision <script>alert(1)</script>');
    await page.getByLabel('Find a project collaborator',{exact:true}).fill('Synthetic comment recipient');await page.getByRole('button',{name:'Find people',exact:true}).click();
    await page.getByRole('button',{name:'Mention Synthetic comment recipient',exact:true}).click();
    await page.getByRole('button',{name:'Post comment',exact:true}).click();await page.locator('.comment-body').filter({hasText:'Release decision'}).waitFor();
    assert.equal(await page.locator('.comment-body script').count(),0);assert.equal(await page.getByText('@Synthetic comment recipient',{exact:true}).count(),1);
    await scan('posted');report.checks.push('explicit project-local mention picker and escaped plain text post');
    stage='owner-edit';await page.getByRole('button',{name:'Edit comment',exact:true}).click();
    const edit=page.getByRole('form',{name:'Edit comment',exact:true}),input=edit.getByLabel('Edit your comment',{exact:true});await input.fill((await input.inputValue()).replace('Release decision','Updated decision'));
    await edit.getByRole('button',{name:'Save comment',exact:true}).click();await page.locator('.comment-body').filter({hasText:'Updated decision'}).waitFor();
    await page.getByRole('button',{name:'Revision history',exact:true}).click();await page.locator('.comment-history').getByText(/Edited by Synthetic comment owner/).waitFor();
    await scan('edited-history');
    stage='owner-delete';await page.getByRole('button',{name:'Delete comment',exact:true}).click();await page.getByRole('button',{name:'Confirm comment deletion',exact:true}).click();await page.getByText('Comment deleted.',{exact:true}).waitFor();
    await page.getByRole('button',{name:'Revision history',exact:true}).click();await page.locator('.comment-history').getByText(/Deleted by Synthetic comment owner/).waitFor();
    assert.equal(await page.locator('.comment-body').filter({hasText:'Updated decision'}).count(),0);report.checks.push('author edit, version refresh, metadata-only history and confirmed text-clearing deletion');
    stage='recipient';await page.getByRole('button',{name:'Sign out',exact:true}).click();await page.getByRole('heading',{name:'Sign in to TaskFlow',exact:true}).waitFor();await login(scenario.recipientEmail);await page.waitForURL(url=>url.pathname!='/login');await page.goto(origin+task);
    await page.getByText('Comment deleted.',{exact:true}).waitFor();assert.equal(await page.getByRole('button',{name:'Edit comment',exact:true}).count(),0);assert.equal(await page.getByRole('button',{name:'Delete comment',exact:true}).count(),0);
    await scan('recipient-tombstone');report.checks.push('account switch clears author draft/actions and recipient reads only tombstone');assert.equal(report.errors.length,0);
    fs.writeFileSync(artifact('comments-report.json'),JSON.stringify(report,null,2));console.log('Published comment journeys and desktop/mobile accessibility passed.');
  } catch (error) { report.errors.push('Journey failed; private diagnostics withheld');fs.writeFileSync(artifact('comments-report.json'),JSON.stringify({...report,stage},null,2));console.error('Comment browser verification failed during '+stage+'; private details withheld.');process.exitCode=1; }
  finally {await browser.close();}
})();
