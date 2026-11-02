const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } }), page = await context.newPage();
    page.on('pageerror', e => report.errors.push(e.message));
    const company = process.env.TASKFLOW_LIFECYCLE_COMPANY_ID, base = '/companies/' + company, api = '/api/v2/workspaces/' + company;
    await page.goto(origin + base + '/policy'); await page.getByLabel('Email address').fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_EMAIL);
    await page.getByLabel('Password', { exact: true }).fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_PASSWORD); await submitLogin(page);
    await page.getByRole('heading', { name: 'Policy & capacity', exact: true }).waitFor(); await page.getByLabel('Audit history review period').waitFor();
    await page.getByText(/Automatic deletion is off/).waitFor();
    const ticket = await page.evaluate(async path => { const csrf = await (await fetch('/api/v1/auth/csrf')).json(); const r = await fetch(path + '/audit/console/exports', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.requestToken }, body: JSON.stringify({ filters: {} }) }); if (r.status !== 200) throw Error('Ticket setup failed'); return (await r.json()).ticket; }, api);
    await page.getByLabel('Audit history review period').selectOption('180'); await page.getByLabel('Allow company audit downloads').uncheck();
    await page.getByLabel('Current password').fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_PASSWORD);
    await page.getByRole('button', { name: 'Review change', exact: true }).click();
    const saved = page.waitForResponse(r => r.request().method() === 'PUT' && r.url().endsWith('/policy'));
    await page.getByRole('button', { name: 'Confirm change', exact: true }).click(); assert.equal((await saved).status(), 204);
    await page.reload(); await page.getByLabel('Audit history review period').waitFor(); assert.equal(await page.getByLabel('Audit history review period').inputValue(), '180'); assert.equal(await page.getByLabel('Allow company audit downloads').isChecked(), false);
    const denied = await page.evaluate(async ({ path, ticket }) => { const csrf = await (await fetch('/api/v1/auth/csrf')).json(); return (await fetch(path + '/audit/console/exports/download', { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.requestToken }, body: JSON.stringify({ ticket }) })).status; }, { path: api, ticket }); assert.equal(denied, 403);
    async function scan(name) {
      await page.route(origin + '/__pilot_axe.js', r => r.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1000 }); await page.addScriptTag({ url: origin + '/__pilot_axe.js' });
        const result = await page.evaluate(async () => { const a = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: a.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }; });
        report.accessibility.push({ name, width, ...result }); await page.screenshot({ path: artifact('pilot-' + name + '-' + width + '.png'), fullPage: true });
        assert.equal(result.violations.length, 0, JSON.stringify(result.violations)); assert.equal(result.overflow, false);
      }
      await page.setViewportSize({ width: 1440, height: 1000 });
    }
    await scan('policy'); await page.goto(origin + base + '/audit'); await page.getByText(/Audit downloads are disabled/).waitFor(); assert.equal(await page.getByRole('button', { name: 'Download audit JSON', exact: true }).isDisabled(), true);
    const events = await page.evaluate(async path => (await (await fetch(path + '/audit/console?action=CompanyPolicyChanged')).json()).items, api); assert(events.length > 0); assert(events.some(e => e.audit.changes.some(d => d.field === 'auditExportsEnabled' && d.after === 'false')));
    await page.goto(origin + base + '/projects/new'); await page.getByLabel('Project name').fill('Capacity draft remains');
    const rejected = page.waitForResponse(r => r.request().method() === 'POST' && r.url().endsWith('/projects'));
    await page.getByRole('button', { name: 'Create project', exact: true }).click(); const response = await rejected; assert.equal(response.status(), 409); assert.equal((await response.json()).code, 'quota.exceeded');
    assert.equal(await page.getByLabel('Project name').inputValue(), 'Capacity draft remains'); await scan('quota');
    const load = await page.evaluate(async path => {
      const csrf = (await (await fetch('/api/v1/auth/csrf')).json()).requestToken;
      const projects = await (await fetch(path + '/projects?search=Retained%20project%20draft')).json();
      const project = projects.items[0]; if (!project) throw Error('Load fixture project missing');
      const tasks = await (await fetch(path + '/projects/' + project.id + '/tasks')).json(); const task = tasks.items[0]; if (!task) throw Error('Load fixture task missing');
      const records = [];
      async function request(name, url, expected, options) {
        const start = performance.now(), response = await fetch(url, options); const body = await response.json();
        if (!expected.includes(response.status) || response.status === 409 && body.code !== 'resource.conflict') throw Error('Unexpected scoped load outcome');
        records.push({ name, ms: performance.now() - start, conflict: response.status === 409 }); return body;
      }
      for (let i = 0; i < 8; i++) await (await fetch(path + '/dashboard')).json();
      const started = performance.now();
      await Promise.all(Array.from({ length: 8 }, async (_, worker) => {
        for (let i = 0; i < 50; i++) {
          switch ((i + worker) % 5) {
            case 0: await request('projects', path + '/projects?pageSize=20', [200]); break;
            case 1: await request('tasks', path + '/projects/' + project.id + '/tasks?pageSize=20', [200]); break;
            case 2: await request('dashboard', path + '/dashboard', [200]); break;
            case 3: await request('activity', path + '/activity?pageSize=20', [200]); break;
            default: {
              const current = await request('task-read-before-write', path + '/tasks/' + task.id, [200]);
              await request('task-status', path + '/tasks/' + task.id + '/status', [200, 409], { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf, 'X-Expected-Version': current.version }, body: JSON.stringify({ status: current.status === 'Done' ? 'ToDo' : 'Done' }) });
            }
          }
        }
      }));
      const sorted = records.map(r => r.ms).sort((a, b) => a - b);
      return { concurrency: 8, roundsPerWorker: 50, requests: records.length, expectedConflicts: records.filter(r => r.conflict).length, unexpected: 0,
        elapsedMs: performance.now() - started, p50Ms: sorted[Math.floor(sorted.length * .5)], p95Ms: sorted[Math.floor(sorted.length * .95)], maxMs: sorted.at(-1),
        scope: 'Local closed-loop hot-company scoped API regression at the project cap; expected stale revisions are not retried. Not selected-host capacity, fairness or SLA certification.' };
    }, api);
    fs.writeFileSync(artifact('pilot-load.json'), JSON.stringify(load, null, 2)); assert.equal(load.requests, 480); assert.equal(load.unexpected, 0);
    report.checks.push('capacity summary', 'owner proof and confirmed policy change', 'reload persistence', 'old export ticket denied', 'disabled download UI', 'safe policy audit', 'quota rejection retains draft');
    assert.equal(report.errors.length, 0); console.log('Published HTTPS pilot policy, password confirmation, export revocation, quota feedback and four accessibility scans passed.');
  } finally { fs.writeFileSync(artifact('pilot-policy.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(e => { console.error(e); process.exit(1); });
