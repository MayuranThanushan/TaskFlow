const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium);
  const report = { checks: [], accessibility: [], errors: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage(); page.on('pageerror', e => report.errors.push(e.message));
    const company = process.env.TASKFLOW_LIFECYCLE_COMPANY_ID;
    const base = '/companies/' + company;
    await page.goto(origin + base + '/audit');
    await page.getByLabel('Email address').fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_EMAIL);
    await page.getByLabel('Password', { exact: true }).fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_PASSWORD);
    await submitLogin(page); await page.getByRole('heading', { name: 'Company audit', exact: true }).waitFor();
    const governance = await page.evaluate(async c => (await fetch('/api/v2/workspaces/' + c + '/audit')).json(), company);
    assert(governance.items.length > 0); assert(governance.items.every(e => e.audit.schemaVersion === 1 && e.audit.outcome === 'Succeeded' && e.audit.correlationId));
    assert(!JSON.stringify(governance).includes('Alpha project'));
    await page.getByRole('link', { name: 'Projects', exact: true }).first().click();
    await page.getByRole('link', { name: 'Alpha project', exact: true }).click();
    const projectPath = new URL(page.url()).pathname;
    await page.getByRole('link', { name: 'View tasks', exact: true }).click(); await page.getByRole('link', { name: 'New task', exact: true }).click();
    const canary = 'AUDIT_PRIVATE_TITLE_CANARY';
    await page.getByLabel('Task title').fill(canary); await page.getByLabel('Priority', { exact: true }).selectOption('High');
    await page.getByRole('button', { name: 'Create task', exact: true }).click(); await page.getByRole('heading', { name: canary, exact: true }).waitFor();
    await page.getByRole('link', { name: 'Edit task', exact: true }).click(); await page.getByLabel('Priority', { exact: true }).selectOption('Low');
    const saved = page.waitForResponse(r => r.request().method() === 'PATCH' && /\/api\/v2\/workspaces\/\d+\/tasks\/\d+$/.test(r.url()));
    await page.getByRole('button', { name: 'Save changes', exact: true }).click(); const response = await saved; assert.equal(response.status(), 200);
    const correlation = response.headers()['x-trace-id'];
    const apiPath = projectPath.replace('/companies/', '/api/v2/workspaces/') + '/activity';
    const feed = await page.evaluate(async path => (await fetch(path)).json(), apiPath);
    const changed = feed.items.find(e => e.actionType === 'TaskUpdated' && e.audit.correlationId === correlation);
    assert(changed); assert(changed.audit.changes.some(d => d.field === 'priority' && d.before === 'High' && d.after === 'Low'));
    assert(!JSON.stringify(feed.items.map(e => e.audit)).includes(canary));
    await page.goto(origin + projectPath + '/activity'); await page.getByRole('heading', { name: 'Project activity', exact: true }).waitFor();
    await page.locator('.audit-metadata summary').first().click(); await page.getByText(/High.*Low/).waitFor();
    await page.route(origin + '/__audit_axe.js', r => r.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    for (const width of [1440, 320]) {
      await page.setViewportSize({ width, height: 1000 }); await page.addScriptTag({ url: origin + '/__audit_axe.js' });
      const result = await page.evaluate(async () => { const a = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: a.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }; });
      report.accessibility.push({ width, ...result }); await page.screenshot({ path: artifact('structured-audit-' + width + '.png'), fullPage: true });
      assert.equal(result.violations.length, 0, JSON.stringify(result.violations)); assert.equal(result.overflow, false);
    }
    report.checks.push('versioned governance', 'safe task deltas', 'request correlation', 'private value redaction', 'expanded audit UI');
    assert.equal(report.errors.length, 0); console.log('Published HTTPS structured audit: versioning, correlation, safe deltas, private-value redaction and two accessibility scans passed.');
  } finally { fs.writeFileSync(artifact('structured-audit.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(e => { console.error(e); process.exit(1); });
