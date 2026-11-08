const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium);
  const report = { checks: [], accessibility: [], errors: [] };
  try {
    const ownerContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const owner = await ownerContext.newPage(); owner.on('pageerror', e => report.errors.push(e.message));
    const memberContext = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const member = await memberContext.newPage(); member.on('pageerror', e => report.errors.push(e.message));
    async function login(page, email, password, path) {
      await page.goto(origin + path); await page.getByLabel('Email address').fill(email);
      await page.getByLabel('Password', { exact: true }).fill(password); await submitLogin(page);
    }
    const company = process.env.TASKFLOW_LIFECYCLE_COMPANY_ID, target = process.env.TASKFLOW_LIFECYCLE_MEMBER_ID;
    assert.match(company, /^[1-9][0-9]*$/); assert.match(target, /^[1-9][0-9]*$/);
    const base = '/companies/' + company;
    await login(owner, process.env.PROJECT_OWNER_EMAIL, process.env.PROJECT_OWNER_PASSWORD, base + '/team');
    await owner.getByRole('heading', { name: 'Company team', exact: true }).waitFor();
    async function scan(page, name) {
      await page.route(origin + '/__lifecycle_axe.js', r => r.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1000 }); await page.addScriptTag({ url: origin + '/__lifecycle_axe.js' });
        const result = await page.evaluate(async () => { const a = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: a.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }; });
        report.accessibility.push({ name, width, ...result }); await page.screenshot({ path: artifact('lifecycle-' + name + '-' + width + '.png'), fullPage: true });
        assert.equal(result.violations.length, 0, JSON.stringify(result.violations)); assert.equal(result.overflow, false);
      }
      await page.setViewportSize({ width: 1440, height: 1000 });
    }
    await owner.getByText('Lifecycle teammate', { exact: true }).waitFor(); await scan(owner, 'team');
    await owner.getByRole('link', { name: 'Projects', exact: true }).first().click();
    await owner.getByRole('link', { name: 'Alpha project', exact: true }).click();
    const projectPath = new URL(owner.url()).pathname;
    await owner.getByLabel('Existing user ID').fill(target); await owner.getByLabel('Project access', { exact: true }).selectOption('Viewer');
    await owner.getByRole('button', { name: 'Add member', exact: true }).click(); await owner.getByText('Member added.', { exact: true }).waitFor();
    await login(member, process.env.TASKFLOW_LIFECYCLE_MEMBER_EMAIL, process.env.TASKFLOW_LIFECYCLE_MEMBER_PASSWORD, projectPath + '/tasks');
    await member.getByText('1 task', { exact: true }).waitFor(); assert.equal(await member.getByRole('link', { name: 'New task', exact: true }).count(), 0);
    await member.getByRole('link', { name: 'Company task', exact: true }).click();
    const taskPath = new URL(member.url()).pathname;
    await member.getByRole('heading', { name: 'Company task', exact: true }).waitFor();
    assert.equal(await member.getByRole('link', { name: 'Edit task', exact: true }).count(), 0);
    assert.equal(await member.getByRole('button', { name: 'Update status', exact: true }).count(), 0); await scan(member, 'viewer');
    const projectRole = owner.getByRole('form', { name: 'Change project access for Lifecycle teammate', exact: true });
    await projectRole.getByLabel('Project role').selectOption('Editor');
    await projectRole.getByRole('button', { name: 'Review change' }).click(); await owner.getByRole('button', { name: 'Confirm change', exact: true }).click();
    await owner.locator('.member-list li').filter({ hasText: 'Lifecycle teammate' }).getByText(/^Editor \| User ID:/).waitFor();
    await member.reload(); await member.getByRole('link', { name: 'Edit task', exact: true }).waitFor();
    await owner.goto(origin + taskPath + '/edit'); await owner.getByLabel('Assignee', { exact: true }).selectOption(target);
    await owner.getByRole('button', { name: 'Save changes', exact: true }).click(); await owner.getByRole('heading', { name: 'Company task', exact: true }).waitFor();
    await owner.goto(origin + base + '/team');
    async function stateChange(state) {
      const form = owner.getByRole('form', { name: 'Change access for Lifecycle teammate', exact: true });
      await form.getByLabel('Membership state').selectOption(state); await form.getByRole('button', { name: 'Review change' }).click();
      await owner.getByRole('button', { name: 'Confirm change', exact: true }).click();
      await owner.getByRole('heading', { name: 'Company team', exact: true }).waitFor();
      await owner.locator('.member-list li').filter({ hasText: 'Lifecycle teammate' }).getByText(new RegExp('· ' + state + ' ·')).waitFor();
    }
    await stateChange('Suspended'); await member.reload(); await member.getByRole('heading', { name: 'Company not available.', exact: true }).waitFor();
    await stateChange('Active'); await member.reload(); await member.getByRole('heading', { name: 'Company task', exact: true }).waitFor();
    assert.equal(await member.getByText('Unassigned', { exact: true }).count(), 1);
    await owner.goto(origin + projectPath);
    const transferProject = owner.getByRole('form', { name: 'Transfer project ownership', exact: true });
    await transferProject.getByLabel('New project owner ID').fill(target); await transferProject.getByLabel('Current password').fill(process.env.PROJECT_OWNER_PASSWORD);
    await transferProject.getByRole('button', { name: 'Review change' }).click(); await owner.getByRole('button', { name: 'Confirm change', exact: true }).click();
    await owner.getByRole('heading', { name: 'Alpha project', exact: true }).waitFor();
    await owner.waitForFunction(() => ![...document.querySelectorAll('legend')].some(e => e.textContent === 'Transfer project ownership'));
    await owner.goto(origin + base + '/team');
    const transferCompany = owner.getByRole('form', { name: 'Transfer company ownership', exact: true });
    await transferCompany.getByLabel('New company owner ID').fill(target); await transferCompany.getByLabel('Current password').fill(process.env.PROJECT_OWNER_PASSWORD);
    await transferCompany.getByRole('button', { name: 'Review change' }).click(); await owner.getByRole('button', { name: 'Confirm change', exact: true }).click();
    await owner.locator('.company-switchbar').getByText('Admin', { exact: true }).waitFor();
    const leave = owner.getByRole('form', { name: 'Leave company', exact: true });
    await leave.getByRole('button', { name: 'Review change' }).click(); await owner.getByRole('button', { name: 'Confirm change', exact: true }).click();
    await owner.getByRole('heading', { name: 'Companies', exact: true }).waitFor();
    await owner.goto(origin + projectPath); await owner.getByRole('heading', { name: 'Company not available.', exact: true }).waitFor();
    await member.goto(origin + base + '/audit'); await member.getByRole('heading', { name: 'Company audit', exact: true }).waitFor();
    await member.getByRole('cell', { name: 'CompanyOwnershipTransferred', exact: true }).waitFor();
    assert.equal(await member.getByText('Alpha project', { exact: true }).count(), 0); await scan(member, 'audit');
    report.checks.push('Viewer read-only controls', 'Editor upgrade', 'eligible assignment', 'suspension and immediate request denial', 'reactivation without assignment restoration', 'password-protected project transfer', 'password-protected company transfer', 'former owner departure', 'removed member denied', 'redacted retained audit');
    assert.equal(report.errors.length, 0); console.log('Published HTTPS lifecycle: Viewer/Editor, assignment, suspension/reactivation, project/company transfer, departure, audit and six accessibility scans passed.');
  } finally { fs.writeFileSync(artifact('lifecycle.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(e => { console.error(e); process.exit(1); });
