const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] };
  try {
    const links = JSON.parse(fs.readFileSync(process.env.TASKFLOW_ACCOUNT_LINKS_FILE, 'utf8'));
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } }), page = await context.newPage();
    page.on('pageerror', () => report.errors.push('Browser script error; private diagnostics withheld'));
    await page.route(origin + '/__account_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    async function scan(name) {
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1000 }); await page.addScriptTag({ url: origin + '/__account_axe.js' });
        const result = await page.evaluate(async () => { const audit = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: audit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }; });
        report.accessibility.push({ name, width, ...result }); assert.equal(result.violations.length, 0); assert.equal(result.overflow, false);
      }
      await page.setViewportSize({ width: 1440, height: 1000 });
    }
    async function write(path, data) {
      for (let attempt = 0; attempt < 2; attempt++) {
        const response = await page.evaluate(async ({ path, data }) => { const token = await (await fetch('/api/v1/auth/csrf')).json(); const result = await fetch('/api/v1/auth/' + path, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token.requestToken }, body: JSON.stringify(data) }); return { status: result.status, retry: Number(result.headers.get('retry-after')) }; }, { path, data });
        if (response.status !== 429) return response.status;
        assert.ok(response.retry >= 0 && response.retry <= 60); await page.waitForTimeout((response.retry + 1) * 1000);
      }
      throw Error('Account mutation remained throttled');
    }
    report.checks.push('opening verification preview');
    await page.goto(links.verify); await page.getByRole('button', { name: 'Verify email', exact: true }).waitFor();
    await page.waitForURL(origin + '/verify-email'); assert.equal(new URL(page.url()).hash, '');
    report.checks.push('verification loaded with fragment removed');
    await scan('verify-confirmation'); await page.getByRole('button', { name: 'Verify email', exact: true }).click(); await page.getByText('Email verified. Sign in to continue.', { exact: true }).waitFor();
    report.checks.push('private fragment removed; explicit email verification succeeded');
    await page.goto(origin + '/login'); await page.getByLabel('Email address').fill(process.env.PROJECT_OWNER_EMAIL); await page.getByLabel('Password', { exact: true }).fill(process.env.PROJECT_OWNER_PASSWORD); await submitLogin(page);
    await page.goto(origin + '/account-security'); await page.getByText('Email: Verified', { exact: true }).waitFor();
    await scan('security-settings'); await page.getByLabel('Current password').fill(process.env.PROJECT_OWNER_PASSWORD); await page.getByRole('button', { name: 'Confirm identity only' }).click(); await page.getByText('Identity confirmed for five minutes.', { exact: true }).waitFor();
    report.checks.push('verified status and five-minute identity proof');
    // Respect the shared per-account resend cooldown after verification.
    await page.waitForTimeout(61000);
    assert.equal(await write('forgot-password', { email: process.env.PROJECT_OWNER_EMAIL }), 202);
    // Explicit test harness invokes a private worker, then supplies a captured reset link.
    fs.writeFileSync(process.env.TASKFLOW_ACCOUNT_REQUESTED_FILE, 'ready');
    const deadline = Date.now() + 120000;
    while (!fs.existsSync(process.env.TASKFLOW_ACCOUNT_RESET_FILE)) { if (Date.now() > deadline) throw Error('Capture handoff timed out'); await page.waitForTimeout(100); }
    const reset = JSON.parse(fs.readFileSync(process.env.TASKFLOW_ACCOUNT_RESET_FILE, 'utf8'));
    await page.goto(origin + '/forgot-password');
    await page.goto(reset.link); await page.getByLabel('New password', { exact: true }).waitFor(); await page.waitForURL(origin + '/reset-password'); await scan('password-reset');
    await page.getByLabel('New password', { exact: true }).fill(process.env.TASKFLOW_ACCOUNT_NEW_PASSWORD); await page.getByLabel('Confirm new password').fill(process.env.TASKFLOW_ACCOUNT_NEW_PASSWORD); await page.getByRole('button', { name: 'Reset password', exact: true }).click();
    report.checks.push('first reset submitted');
    await page.getByText('Password reset. Sign in with your new password.', { exact: true }).waitFor(); report.checks.push('first reset completed');
    await page.goto(origin + '/forgot-password');
    await page.goto(reset.link); await page.getByLabel('New password', { exact: true }).fill(process.env.TASKFLOW_ACCOUNT_NEW_PASSWORD); await page.getByLabel('Confirm new password').fill(process.env.TASKFLOW_ACCOUNT_NEW_PASSWORD); await page.getByRole('button', { name: 'Reset password', exact: true }).click(); await page.getByRole('alert').filter({ hasText: 'invalid or expired' }).waitFor();
    report.checks.push('captured reset succeeds and replay is rejected');
    await page.goto(origin + '/login'); await page.getByLabel('Email address').fill(process.env.PROJECT_OWNER_EMAIL); await page.getByLabel('Password', { exact: true }).fill(process.env.TASKFLOW_ACCOUNT_NEW_PASSWORD); await submitLogin(page);
    await page.goto(origin + '/account-security'); await page.getByLabel('Current password').fill(process.env.TASKFLOW_ACCOUNT_NEW_PASSWORD); await page.getByLabel('New password', { exact: true }).fill(process.env.TASKFLOW_ACCOUNT_CHANGED_PASSWORD); await page.getByLabel('Confirm new password').fill(process.env.TASKFLOW_ACCOUNT_CHANGED_PASSWORD); await page.getByRole('button', { name: 'Change password', exact: true }).click(); await page.getByRole('heading', { name: 'Sign in to TaskFlow', exact: true }).waitFor();
    report.checks.push('password change signs out current session');
    await page.goto(origin + '/forgot-password'); await scan('recovery-request');
    assert.deepEqual(report.errors, []);
    fs.writeFileSync(artifact('account-security.json'), JSON.stringify(report, null, 2));
    console.log('Published HTTPS account verification, encrypted capture, identity proof, reset/replay, password change and eight accessibility/layout scans passed.');
  } finally { fs.writeFileSync(artifact('account-security.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(() => { console.error('Account browser verification failed; private diagnostics withheld.'); process.exitCode = 1; });
