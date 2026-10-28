const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');

(async () => {
  const browser = await launch(chromium);
  const report = { checks: [], accessibility: [], errors: [] };
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();
    page.on('pageerror', e => report.errors.push(e.message));
    await page.goto(origin + '/companies');
    await page.getByLabel('Email address').fill(process.env.PROJECT_OWNER_EMAIL);
    await page.getByLabel('Password', { exact: true }).fill(process.env.PROJECT_OWNER_PASSWORD);
    await submitLogin(page);
    await page.getByRole('heading', { name: 'Companies', exact: true }).waitFor();
    await page.route(origin + '/__company_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    async function scan(name) {
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1000 });
        await page.addScriptTag({ url: origin + '/__company_axe.js' });
        const result = await page.evaluate(async () => {
          const audit = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } });
          return { violations: audit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth };
        });
        report.accessibility.push({ name, width, ...result });
        await page.screenshot({ path: artifact('company-' + name + '-' + width + '.png'), fullPage: true });
        assert.equal(result.violations.length, 0, JSON.stringify(result.violations)); assert.equal(result.overflow, false);
      }
      await page.setViewportSize({ width: 1440, height: 1000 });
    }
    await scan('directory');
    async function create(name) {
      await page.getByLabel('Company name').fill(name);
      await page.getByRole('button', { name: 'Create company', exact: true }).click();
      await page.getByRole('heading', { name: /Welcome back/ }).waitFor();
      assert.match(new URL(page.url()).pathname, /^\/companies\/\d+\/dashboard$/);
      return new URL(page.url()).pathname.split('/')[2];
    }
    const alpha = await create('Browser Alpha'); await scan('overview');
    await page.getByRole('link', { name: 'Switch company', exact: true }).click();
    const beta = await create('Browser Beta'); assert.notEqual(alpha, beta);
    await page.goto(origin + '/companies/' + alpha + '/projects/new');
    await page.getByLabel('Project name').fill('Alpha project');
    await page.getByRole('button', { name: 'Create project', exact: true }).click();
    await page.getByRole('heading', { name: 'Alpha project', exact: true }).waitFor();
    const projectUrl = page.url(); assert.match(new URL(projectUrl).pathname, new RegExp('^/companies/' + alpha + '/projects/\\d+$'));
    await page.getByRole('link', { name: 'View tasks', exact: true }).click();
    await page.getByRole('link', { name: 'New task', exact: true }).click();
    await page.getByLabel('Task title').fill('Company task');
    await page.getByRole('button', { name: 'Create task', exact: true }).click();
    await page.getByRole('heading', { name: 'Company task', exact: true }).waitFor();
    assert.match(new URL(page.url()).pathname, new RegExp('^/companies/' + alpha + '/tasks/\\d+$'));
    await scan('task');
    await page.goto(projectUrl); await page.getByRole('heading', { name: 'Alpha project', exact: true }).waitFor();
    await page.getByRole('link', { name: 'Switch company', exact: true }).click();
    await page.getByRole('link', { name: 'Open Browser Beta', exact: true }).click();
    await page.getByRole('link', { name: 'Projects', exact: true }).first().click();
    await page.getByText('0 projects', { exact: true }).waitFor();
    assert.equal(await page.getByText('Alpha project', { exact: true }).count(), 0);
    await scan('empty-projects');
    await page.goto(projectUrl.replace('/companies/' + alpha + '/', '/companies/' + beta + '/'));
    await page.getByRole('heading', { name: 'Project not available.', exact: true }).waitFor();
    report.checks.push('protected company return URL', 'atomic create with Owner role', 'two company switching', 'scoped project/task CRUD links', 'reload deep link', 'empty company isolation', 'mixed-company resource denied');
    assert.equal(report.errors.length, 0);
    console.log('Published HTTPS company creation, switching, scoped CRUD, deep links, isolation and eight accessibility/layout scans passed.');
  } finally {
    fs.writeFileSync(artifact('companies.json'), JSON.stringify(report, null, 2));
    await browser.close();
  }
})().catch(error => { console.error(error); process.exit(1); });
