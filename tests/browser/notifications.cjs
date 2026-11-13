const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const scenario = JSON.parse(fs.readFileSync(process.env.TASKFLOW_NOTIFICATION_SCENARIO, 'utf8'));
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] };
  let stage = 'start';
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1050 } }), page = await context.newPage();
    page.on('pageerror', () => report.errors.push('Browser script error; private details withheld'));
    await context.route(origin + '/__notification_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    async function scan(name) {
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1050 }); await page.addScriptTag({ url: origin + '/__notification_axe.js' });
        const result = await page.evaluate(async () => {
          const audit = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } });
          return { violations: audit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth };
        });
        report.accessibility.push({ name, width, ...result }); assert.equal(result.violations.length, 0); assert.equal(result.overflow, false);
        await page.screenshot({ path: artifact('notifications-' + name + '-' + width + '.png'), fullPage: true });
      }
      await page.setViewportSize({ width: 1440, height: 1050 });
    }
    async function login(email) { await page.getByLabel('Email address').fill(email); await page.getByLabel('Password', { exact: true }).fill(scenario.password); await submitLogin(page); }
    const inbox = '/companies/' + scenario.workspaceId + '/notifications';
    stage = 'recipient'; await page.goto(origin + inbox); await login(scenario.recipientEmail);
    await page.getByRole('heading', { name: 'Inbox', exact: true }).waitFor(); await page.getByRole('link', { name: 'Private smoke task', exact: true }).waitFor();
    await scan('unread');
    await page.getByRole('button', { name: 'Mark Private smoke task as read', exact: true }).click(); await page.getByRole('button', { name: 'Mark Private smoke task as unread', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Mark Private smoke task as unread', exact: true }).click(); await page.getByRole('button', { name: 'Mark Private smoke task as read', exact: true }).waitFor();
    await page.getByRole('button', { name: 'Mark page as read', exact: true }).click(); await page.getByText('0 unread notifications', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Unread', exact: true }).click(); await page.getByRole('heading', { name: 'You’re all caught up', exact: true }).waitFor();
    await page.getByRole('button', { name: 'All notifications', exact: true }).click(); await page.getByRole('link', { name: 'Private smoke task', exact: true }).waitFor();
    await page.getByRole('link', { name: 'Private smoke task', exact: true }).click(); await page.waitForURL(origin + '/companies/' + scenario.workspaceId + '/tasks/' + scenario.taskId);
    await page.goBack(); await page.getByRole('heading', { name: 'Inbox', exact: true }).waitFor();
    report.checks.push('authenticated scoped inbox, reversible read state, bounded page read, unread filter and task deep link');
    stage = 'preferences'; const summary = page.locator('summary', { hasText: 'Notification preferences' }); await summary.focus(); await page.keyboard.press('Enter');
    assert.equal(await page.getByLabel('Email notifications', { exact: true }).isDisabled(), true);
    await page.getByLabel('In-app notifications', { exact: true }).uncheck(); assert.equal(await page.getByLabel('Tasks assigned to me', { exact: true }).isDisabled(), true);
    await page.getByRole('button', { name: 'Save preferences', exact: true }).click(); await page.getByText('Notification preferences saved.', { exact: true }).waitFor();
    await scan('muted-preferences');
    await page.reload(); await page.getByRole('heading', { name: 'Inbox', exact: true }).waitFor(); await page.locator('summary', { hasText: 'Notification preferences' }).click();
    await page.getByRole('button', { name: 'Save preferences', exact: true }).waitFor(); assert.equal(await page.getByLabel('In-app notifications', { exact: true }).isChecked(), false);
    await page.getByLabel('In-app notifications', { exact: true }).check();
    await page.getByLabel('Remind me when assigned tasks are due', { exact: true }).check();
    await page.getByLabel('Daily digest of overdue tasks and tasks due in the next 24 hours', { exact: true }).check();
    await page.getByLabel('Timezone', { exact: true }).fill('Asia/Colombo'); await page.getByLabel('Daily digest hour', { exact: true }).selectOption('17');
    await page.getByRole('button', { name: 'Save preferences', exact: true }).click(); await page.getByText('Notification preferences saved.', { exact: true }).waitFor();
    await page.reload(); await page.getByRole('heading', { name: 'Inbox', exact: true }).waitFor(); await page.locator('summary', { hasText: 'Notification preferences' }).click();
    await page.getByRole('button', { name: 'Save preferences', exact: true }).waitFor();
    assert.equal(await page.getByLabel('Remind me when assigned tasks are due', { exact: true }).isChecked(), true);
    assert.equal(await page.getByLabel('Timezone', { exact: true }).inputValue(), 'Asia/Colombo'); assert.equal(await page.getByLabel('Daily digest hour', { exact: true }).inputValue(), '17');
    await scan('scheduled-preferences');
    report.checks.push('keyboard preferences, unavailable email channel, persisted company mute and explicit re-enable');
    stage = 'other-account'; await page.getByRole('button', { name: 'Sign out', exact: true }).click(); await page.getByRole('heading', { name: 'Sign in to TaskFlow', exact: true }).waitFor();
    await login(scenario.ownerEmail); await page.waitForURL(url => url.pathname !== '/login'); await page.goto(origin + inbox);
    await page.getByRole('heading', { name: 'Your inbox is clear', exact: true }).waitFor(); assert.equal(await page.getByRole('link', { name: 'Private smoke task', exact: true }).count(), 0);
    await scan('owner-empty'); report.checks.push('account switch clears prior recipient inbox and owner cannot view recipient items');
    assert.equal(report.errors.length, 0);
    fs.writeFileSync(artifact('notifications-report.json'), JSON.stringify(report, null, 2)); console.log('Published inbox journey and desktop/mobile accessibility checks passed.');
  } catch (error) {
    report.errors.push('Journey failed; private diagnostics withheld'); fs.writeFileSync(artifact('notifications-report.json'), JSON.stringify({ ...report, stage }, null, 2));
    console.error('Notification browser verification failed during ' + stage + '; private details withheld.'); process.exitCode = 1;
  } finally { await browser.close(); }
})();
