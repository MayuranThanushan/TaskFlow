const assert = require('node:assert/strict');
const fs = require('node:fs');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] };
  try {
    const scenario = JSON.parse(fs.readFileSync(process.env.TASKFLOW_INVITATION_SCENARIO, 'utf8'));
    const context = await browser.newContext({ viewport: { width: 1440, height: 1050 } }), page = await context.newPage();
    page.on('pageerror', () => report.errors.push('Browser script error; private details withheld'));
    await context.route(origin + '/__invite_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    async function scan(name) {
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1050 }); await page.addScriptTag({ url: origin + '/__invite_axe.js' });
        const result = await page.evaluate(async () => { const audit = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: audit.violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }; });
        report.accessibility.push({ name, width, ...result }); await page.screenshot({ path: artifact('invitation-' + name + '-' + width + '.png'), fullPage: true }); assert.equal(result.violations.length, 0); assert.equal(result.overflow, false);
      }
      await page.setViewportSize({ width: 1440, height: 1050 });
    }
    async function handoff(stage) {
      fs.writeFileSync(scenario.requestFile, stage);
      const target = scenario.handoffDirectory + '/' + stage + '.json', until = Date.now() + 120000;
      while (!fs.existsSync(target)) { if (Date.now() > until) throw Error('Private capture handoff timeout'); await page.waitForTimeout(100); }
      return JSON.parse(fs.readFileSync(target, 'utf8'));
    }
    async function login(email, password) { await page.getByLabel('Email address').fill(email); await page.getByLabel('Password', { exact: true }).fill(password); await submitLogin(page); }
    await page.goto(origin + '/companies/' + scenario.workspaceId + '/team'); await login(scenario.ownerEmail, scenario.password);
    await page.getByRole('heading', { name: 'Company team', exact: true }).waitFor(); await page.getByRole('link', { name: 'Manage invitations', exact: true }).click();
    await page.getByRole('heading', { name: 'Company invitations', exact: true }).waitFor(); await scan('company-empty');
    await page.getByLabel('Recipient email').fill(scenario.revokedEmail); await page.getByRole('button', { name: 'Create invitation', exact: true }).click(); await page.getByRole('button', { name: 'Revoke for ' + scenario.revokedEmail }).waitFor();
    await page.getByRole('button', { name: 'Revoke for ' + scenario.revokedEmail }).click(); await page.getByRole('button', { name: 'Keep invitation', exact: true }).focus(); await page.keyboard.press('Escape');
    assert.equal(await page.getByRole('button', { name: 'Revoke invitation', exact: true }).count(), 0);
    await page.getByRole('button', { name: 'Revoke for ' + scenario.revokedEmail }).click(); await page.getByRole('button', { name: 'Revoke invitation', exact: true }).click(); await page.getByText('Invitation revoked. Its link no longer grants access.', { exact: true }).waitFor();
    report.checks.push('company creation, explicit revoke and keyboard cancellation');
    await page.goto(origin + '/companies/' + scenario.workspaceId + '/projects/' + scenario.projectId); await page.getByRole('link', { name: 'Manage project invitations', exact: true }).click();
    await page.getByRole('heading', { name: 'Project invitations', exact: true }).waitFor();
    const roles = await page.getByLabel('Role', { exact: true }).locator('option').allTextContents(); assert.deepEqual(roles, ['Viewer', 'Editor']);
    await page.getByLabel('Recipient email').fill(scenario.recipientEmail); await page.getByRole('button', { name: 'Create invitation', exact: true }).click(); await page.getByRole('button', { name: 'Resend to ' + scenario.recipientEmail }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Resend to ' + scenario.recipientEmail }).isDisabled(), true);
    await scan('project-pending');
    const first = await handoff('invite-first'); await page.getByRole('button', { name: 'Refresh invitations', exact: true }).click(); await page.getByText('Delivery: Preview captured (email not sent)', { exact: true }).waitFor();
    await page.getByRole('button', { name: 'Resend to ' + scenario.recipientEmail }).click(); await page.getByRole('button', { name: 'Resend invitation', exact: true }).click(); await page.getByText('A new invitation was queued. The previous link no longer works.', { exact: true }).waitFor();
    const second = await handoff('invite-second'); assert.notEqual(first.link, second.link);
    report.checks.push('project role choices, reserved capacity, resend cooldown and explicit token rotation');
    await page.goto(second.link); await page.getByRole('alert').filter({ hasText: 'unavailable for this account' }).waitFor(); assert.equal(await page.getByText('Browser private project', { exact: true }).count(), 0); await scan('wrong-account');
    await page.getByRole('button', { name: 'Use another account', exact: true }).click(); await page.getByRole('heading', { name: 'Sign in to TaskFlow', exact: true }).waitFor(); await page.getByRole('link', { name: 'Create an account', exact: true }).click();
    await page.getByLabel('Your name').fill('Invited teammate'); await page.getByLabel('Email address').fill(scenario.recipientEmail); await page.getByLabel('Password', { exact: true }).fill(scenario.password); await page.getByRole('button', { name: 'Create account', exact: true }).click();
    await page.getByRole('heading', { name: 'Sign in to TaskFlow', exact: true }).waitFor(); await login(scenario.recipientEmail, scenario.password);
    await page.getByText('Verify your account email before accepting this invitation.', { exact: true }).waitFor(); assert.equal(new URL(page.url()).hash, ''); await scan('verification-required');
    await page.getByRole('link', { name: 'Verify your email', exact: true }).click(); await page.getByLabel('Email address').fill(scenario.recipientEmail); await page.getByRole('button', { name: 'Request link', exact: true }).click(); await page.getByRole('status').filter({ hasText: 'If the address is eligible' }).waitFor();
    const verification = await handoff('verify-account'), verifier = await context.newPage();
    await verifier.goto(verification.link); await verifier.getByRole('button', { name: 'Verify email', exact: true }).click(); await verifier.getByText('Email verified. Sign in to continue.', { exact: true }).waitFor(); await verifier.close();
    await page.bringToFront(); await page.evaluate(() => window.dispatchEvent(new Event('focus'))); await page.waitForTimeout(300);
    await page.getByRole('link', { name: 'Back to sign in', exact: true }).click(); await page.getByRole('heading', { name: 'Sign in to TaskFlow', exact: true }).waitFor(); await login(scenario.recipientEmail, scenario.password);
    await page.getByText('Browser private project', { exact: true }).waitFor(); assert.equal(new URL(page.url()).hash, ''); await scan('scope-preview');
    const before = await page.evaluate(async workspace => (await fetch('/api/v2/workspaces/' + workspace)).status, scenario.workspaceId); assert.equal(before, 404);
    await page.getByRole('button', { name: 'Accept invitation', exact: true }).focus(); await page.keyboard.press('Enter'); await page.getByRole('heading', { name: 'Invitation accepted', exact: true }).waitFor(); await scan('accepted'); await page.getByRole('link', { name: 'Open project', exact: true }).click();
    await page.getByRole('heading', { name: 'Browser private project', exact: true }).waitFor(); assert.equal(await page.getByRole('link', { name: 'Manage project invitations', exact: true }).count(), 0);
    report.checks.push('wrong account, signup/sign-in return, new-tab verification, explicit preview and accepted Guest/Viewer access');
    await page.goto(second.link); await page.getByRole('alert').filter({ hasText: 'unavailable for this account' }).waitFor(); await scan('used-link');
    await page.goto(origin + '/invitations/accept'); await page.getByRole('heading', { name: 'Open your invitation', exact: true }).waitFor(); await scan('missing-link');
    const storage = await page.evaluate(() => ({ local: Object.keys(localStorage), session: Object.keys(sessionStorage) })); assert.deepEqual(storage, { local: [], session: [] });
    report.checks.push('replay/missing-link recovery, fragment clearing and no browser token storage'); assert.deepEqual(report.errors, []);
    console.log('Published invitation/team browser journey and sixteen accessibility/layout scans passed.');
  } finally { fs.writeFileSync(artifact('invitations.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(() => { console.error('Invitation browser verification failed; private diagnostics withheld.'); process.exitCode = 1; });
