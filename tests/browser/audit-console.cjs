const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, acceptDownloads: true });
    const page = await context.newPage(); page.on('pageerror', e => report.errors.push(e.message));
    const company = process.env.TASKFLOW_LIFECYCLE_COMPANY_ID, base = '/companies/' + company;
    await page.goto(origin + base + '/audit');
    await page.getByLabel('Email address').fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_EMAIL);
    await page.getByLabel('Password', { exact: true }).fill(process.env.TASKFLOW_LIFECYCLE_MEMBER_PASSWORD);
    await submitLogin(page); await page.getByRole('heading', { name: 'Company audit', exact: true }).waitFor();
    await page.getByRole('columnheader', { name: 'Action', exact: true }).waitFor();
    await page.getByLabel('Event source').selectOption('Projects');
    await page.getByLabel('Action', { exact: true }).fill('TaskUpdated');
    await page.getByRole('button', { name: 'Apply filters', exact: true }).click();
    await page.getByRole('cell', { name: 'TaskUpdated', exact: true }).first().waitFor();
    assert(new URL(page.url()).searchParams.get('action') === 'TaskUpdated');
    await page.locator('.audit-metadata summary').first().click(); await page.getByText(/High.*Low/).waitFor();
    const downloaded = page.waitForEvent('download'); await page.getByRole('button', { name: 'Download audit JSON', exact: true }).click();
    const download = await downloaded; assert.equal(download.suggestedFilename(), 'taskflow-audit.json');
    const data = JSON.parse(fs.readFileSync(await download.path(), 'utf8'));
    assert(data.items.length > 0 && data.items.length <= 1000); assert(data.items.every(e => typeof e.id === 'string' && e.action === 'TaskUpdated'));
    assert(!JSON.stringify(data).includes('AUDIT_PRIVATE_TITLE_CANARY')); assert(!JSON.stringify(data).includes('PasswordProof'));
    await page.route(origin + '/__console_axe.js', r => r.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    for (const width of [1440, 320]) {
      await page.setViewportSize({ width, height: 1000 }); await page.addScriptTag({ url: origin + '/__console_axe.js' });
      const result = await page.evaluate(async () => { const a = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: a.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }; });
      report.accessibility.push({ width, ...result }); await page.screenshot({ path: artifact('audit-console-' + width + '.png'), fullPage: true });
      assert.equal(result.violations.length, 0, JSON.stringify(result.violations)); assert.equal(result.overflow, false);
    }
    await page.getByRole('button', { name: 'Clear filters' }).click();
    await page.getByLabel('Event source').selectOption('Projects'); await page.getByLabel('Project state').selectOption('Deleted');
    await page.getByRole('button', { name: 'Apply filters', exact: true }).click(); await page.getByText('No audit events match these filters.', { exact: true }).waitFor();
    report.checks.push('intersected URL filters', 'expanded safe deltas', 'authenticated JSON download', 'exact string IDs', 'private value redaction', 'deleted-only empty state');
    assert.equal(report.errors.length, 0); console.log('Published HTTPS audit console: filtered history, safe JSON download, deleted-only view and two accessibility scans passed.');
  } finally { fs.writeFileSync(artifact('audit-console.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(e => { console.error(e); process.exit(1); });
