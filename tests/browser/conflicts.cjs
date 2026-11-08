const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const one = await context.newPage(), two = await context.newPage();
    for (const page of [one, two]) page.on('pageerror', e => report.errors.push(e.message));
    await one.goto(origin + '/companies/' + process.env.TASKFLOW_LIFECYCLE_COMPANY_ID + '/projects');
    await one.getByLabel('Email address').fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_EMAIL);
    await one.getByLabel('Password', { exact: true }).fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_PASSWORD); await submitLogin(one);
    await one.getByRole('link', { name: 'Alpha project', exact: true }).click(); const project = new URL(one.url()).pathname;
    await one.getByRole('link', { name: 'Edit project', exact: true }).click(); await one.getByLabel('Project name').waitFor();
    await two.goto(origin + project + '/edit'); await two.getByLabel('Project name').waitFor();
    await two.getByLabel('Project name').fill('Retained project draft');
    await one.getByLabel('Description', { exact: false }).fill('Concurrent server brief');
    await one.getByRole('button', { name: 'Save changes', exact: true }).click(); await one.getByRole('heading', { name: 'Alpha project', exact: true }).waitFor();
    async function stale(page, method, button) {
      const response = page.waitForResponse(r => r.request().method() === method && /\/api\/v2\/workspaces\//.test(r.url()));
      await page.getByRole('button', { name: button, exact: true }).click();
      const result = await response; assert.equal(result.status(), 409); assert.equal((await result.json()).code, 'resource.conflict');
      assert.match(result.request().headers()['x-expected-version'], /^[1-9][0-9]*$/);
    }
    async function scan(page, name) {
      await page.route(origin + '/__conflict_axe.js', r => r.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1000 }); await page.addScriptTag({ url: origin + '/__conflict_axe.js' });
        const result = await page.evaluate(async () => { const a = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: a.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }; });
        report.accessibility.push({ name, width, ...result }); await page.screenshot({ path: artifact('conflict-' + name + '-' + width + '.png'), fullPage: true });
        assert.equal(result.violations.length, 0, JSON.stringify(result.violations)); assert.equal(result.overflow, false);
      }
      await page.setViewportSize({ width: 1440, height: 1000 });
    }
    await stale(two, 'PUT', 'Save changes'); assert.equal(await two.getByLabel('Project name').inputValue(), 'Retained project draft');
    await two.getByRole('button', { name: 'Compare latest version', exact: true }).click(); await two.getByRole('button', { name: 'Keep my edits', exact: true }).waitFor();
    await scan(two, 'project'); await two.getByRole('button', { name: 'Keep my edits', exact: true }).click();
    assert.equal(await two.getByLabel('Description', { exact: false }).inputValue(), 'Concurrent server brief');
    await two.getByRole('button', { name: 'Save changes', exact: true }).click(); await two.getByRole('heading', { name: 'Retained project draft', exact: true }).waitFor();
    await one.goto(origin + project + '/tasks/new'); await one.getByLabel('Task title').fill('Conflict task'); await one.getByRole('button', { name: 'Create task', exact: true }).click();
    await one.getByRole('heading', { name: 'Conflict task', exact: true }).waitFor(); const task = new URL(one.url()).pathname;
    await one.goto(origin + task + '/edit'); await one.getByLabel('Task title').waitFor(); await two.goto(origin + task + '/edit'); await two.getByLabel('Task title').waitFor();
    await two.getByLabel('Task title').fill('Retained task draft'); await one.getByLabel('Priority', { exact: true }).selectOption('High');
    await one.getByRole('button', { name: 'Save changes', exact: true }).click(); await one.getByRole('heading', { name: 'Conflict task', exact: true }).waitFor();
    await stale(two, 'PATCH', 'Save changes'); assert.equal(await two.getByLabel('Task title').inputValue(), 'Retained task draft');
    await two.getByRole('button', { name: 'Compare latest version', exact: true }).click(); await two.getByRole('button', { name: 'Keep my edits', exact: true }).waitFor();
    await scan(two, 'task'); await two.getByRole('button', { name: 'Keep my edits', exact: true }).click(); assert.equal(await two.getByLabel('Priority', { exact: true }).inputValue(), 'High');
    const saved = two.waitForResponse(r => r.request().method() === 'PATCH' && !r.url().endsWith('/status'));
    await two.getByRole('button', { name: 'Save changes', exact: true }).click(); assert.deepEqual((await saved).request().postDataJSON(), { title: 'Retained task draft' });
    await two.getByRole('heading', { name: 'Retained task draft', exact: true }).waitFor();
    await one.getByRole('button', { name: 'Delete task', exact: true }).click(); await stale(one, 'DELETE', 'Confirm task deletion');
    await one.getByRole('button', { name: 'Reload task before retrying', exact: true }).click(); await one.getByRole('heading', { name: 'Retained task draft', exact: true }).waitFor();
    await two.getByLabel('Task status', { exact: true }).selectOption('Done'); const statusSaved = two.waitForResponse(r => r.request().method() === 'PATCH' && r.url().endsWith('/status')); await two.getByRole('button', { name: 'Update status', exact: true }).click(); assert.equal((await statusSaved).status(), 200);
    await two.getByRole('button', { name: 'Update status', exact: true }).waitFor({ state: 'visible' });
    await one.getByLabel('Task status', { exact: true }).selectOption('InProgress'); await stale(one, 'PATCH', 'Update status');
    const current = await one.evaluate(async path => (await fetch(path.replace('/companies/', '/api/v2/workspaces/'))).json(), task); assert.equal(current.status, 'Done'); assert.equal(current.priority, 'High'); assert.equal(current.title, 'Retained task draft');
    report.checks.push('two project editors reject stale write', 'project draft rebased after comparison', 'two task editors reject stale write', 'untouched priority retained', 'stale deletion rejected', 'stale status rejected');
    assert.equal(report.errors.length, 0); console.log('Published HTTPS two-editor project/task conflicts, retained drafts, safe rebasing, stale delete/status and four accessibility scans passed.');
  } finally { fs.writeFileSync(artifact('conflicts.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(e => { console.error(e); process.exit(1); });
