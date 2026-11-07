const assert = require('node:assert/strict');
const fs = require('node:fs');
const crypto = require('node:crypto');
const { chromium } = require('playwright-core');
const { launch, origin, artifact } = require('./support.cjs');
// Independent authenticator fixture, held only in browser-test memory.
function totp(key) {
  let bits = 0, buffer = 0; const bytes = [];
  for (const c of key) { buffer = (buffer << 5) | 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'.indexOf(c); bits += 5; if (bits >= 8) { bits -= 8; bytes.push((buffer >>> bits) & 255); } }
  const counter = Buffer.alloc(8); counter.writeBigInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const mac = crypto.createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  return String((mac.readUInt32BE(mac[19] & 15) & 0x7fffffff) % 1000000).padStart(6, '0');
}
(async () => {
  const browser = await launch(chromium), report = { checks: [], accessibility: [], errors: [] };
  let stage = 'startup';
  try {
    const scenario = JSON.parse(fs.readFileSync(process.env.TASKFLOW_MFA_SCENARIO, 'utf8'));
    const context = await browser.newContext(), page = await context.newPage(), startedAt = Date.now();
    page.on('pageerror', () => report.errors.push('Browser runtime error; details withheld'));
    await context.route(origin + '/__mfa_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    async function scan(name, screenshot = false) {
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1000 }); await page.addScriptTag({ url: origin + '/__mfa_axe.js' });
        const value = await page.evaluate(async () => { const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } }); return { violations: result.violations.map(v => v.id), overflow: document.documentElement.scrollWidth > innerWidth + 1 }; });
        report.accessibility.push({ name, width, ...value }); assert.deepEqual(value.violations, []); assert.equal(value.overflow, false);
        // Never screenshot setup keys or recovery-code lists.
        if (screenshot) await page.screenshot({ path: artifact(name + '-' + width + '.png'), fullPage: true });
      }
    }
    async function login(mode, value, expected = 200) {
      await page.goto(origin + '/login'); await page.getByLabel('Email address').fill(scenario.email); await page.getByLabel('Password', { exact: true }).fill(scenario.password);
      if (mode) { await page.getByLabel('Additional verification').selectOption(mode); await page.getByLabel(mode === 'recovery' ? 'Recovery code' : 'Authenticator code', { exact: true }).fill(value); }
      const response = page.waitForResponse(r => r.url() === origin + '/api/v1/auth/login' && r.request().method() === 'POST');
      await page.getByRole('button', { name: 'Sign in', exact: true }).click(); assert.equal((await response).status(), expected);
      if (expected === 200) await page.waitForURL(url => url.pathname !== '/login');
    }
    async function write(path, body) {
      // Use the browser's pinned TLS connection and session, retaining normal certificate checks.
      const result = await page.evaluate(async ({ path, body }) => {
        const csrf = await (await fetch('/api/v1/auth/csrf', { credentials: 'same-origin' })).json();
        const response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.requestToken }, body: JSON.stringify(body) });
        return { status: response.status, body: await response.json() };
      }, { path, body });
      return { status: () => result.status, json: async () => result.body };
    }
    async function responseFor(action, click) {
      const response = page.waitForResponse(r => r.url() === origin + '/api/v1/auth/mfa/' + action && r.request().method() === 'POST');
      await click(); const result = await response; assert.equal(result.status(), 200); return result.json();
    }
    async function form(password, code, recovery = false) {
      await page.getByLabel('Current password', { exact: true }).fill(password);
      if (recovery) await page.getByLabel('Use a recovery code to restore or disable MFA').check();
      if (code) await page.getByLabel(recovery ? 'Recovery code' : 'Authenticator code', { exact: true }).fill(code);
    }
    async function acknowledge() { await page.getByRole('button', { name: 'I saved my recovery codes' }).click(); await page.getByText('Enabled', { exact: true }).waitFor(); }
    const companyPath = '/api/v2/workspaces/' + scenario.workspaceId + '/projects';
    stage = 'initial-login'; await login();
    assert.equal((await write(companyPath, {})).status(), 403);
    await page.goto(origin + '/account-security/mfa'); await page.getByRole('button', { name: 'Set up authenticator' }).waitFor(); await scan('mfa-before-enrollment', true);
    stage = 'enrollment'; await form(scenario.password);
    const setup = await responseFor('setup', () => page.getByRole('button', { name: 'Set up authenticator' }).click());
    await scan('mfa-setup'); await form(scenario.password, totp(setup.sharedKey));
    const issued = await responseFor('confirm', () => page.getByRole('button', { name: 'Confirm authenticator' }).click()); assert.equal(issued.recoveryCodes.length, 10);
    await scan('mfa-one-time-codes'); await acknowledge();
    assert.equal((await write(companyPath, {})).status(), 400);
    await scan('mfa-enabled', true);
    stage = 'password-only-denial'; await page.getByRole('button', { name: 'Sign out', exact: true }).click(); await login('', '', 401);
    stage = 'authenticator-login';
    // Confirmation consumed this period. Wait for a genuinely new code rather than replaying it.
    // Respect the real auth limiter, including fixture registration/login; never weaken it for tests.
    await page.waitForTimeout(Math.max(30000 - Date.now() % 30000 + 1000, 61000 - (Date.now() - startedAt))); await login('authenticator', totp(setup.sharedKey));
    await page.getByRole('button', { name: 'Sign out', exact: true }).click();
    stage = 'recovery-login'; await login('recovery', issued.recoveryCodes[0]);
    const denied = await write(companyPath, {}); assert.equal(denied.status(), 403); assert.equal((await denied.json()).code, 'mfa.administration_required');
    stage = 'authenticator-restoration'; await page.goto(origin + '/account-security/mfa'); await form(scenario.password);
    const replacement = await responseFor('setup', () => page.getByRole('button', { name: 'Replace authenticator' }).click());
    await form(scenario.password, totp(replacement.sharedKey));
    const restored = await responseFor('confirm', () => page.getByRole('button', { name: 'Confirm authenticator' }).click()); assert.equal(restored.recoveryCodes.length, 10); await acknowledge();
    assert.equal((await write(companyPath, {})).status(), 400); assert.equal(await page.getByRole('button', { name: 'Disable MFA', exact: true }).isDisabled(), true);
    stage = 'security-proof'; await page.getByRole('link', { name: 'Account security', exact: true }).click(); await page.getByRole('heading', { name: 'Account security', exact: true }).waitFor(); await scan('mfa-account-security', true);
    assert.equal(await page.evaluate(() => Object.keys(localStorage).length + Object.keys(sessionStorage).length), 0);
    report.checks.push('password-only denial and authenticator sign-in', 'session-bound enrollment and one-time recovery display', 'recovery sign-in cannot grant company administration', 'authenticator restoration rotates secrets/codes and restores proof', 'mandatory administrators cannot disable MFA', 'no browser credential storage');
    assert.deepEqual(report.errors, []);
  } catch { report.errors.push('MFA browser journey failed at ' + stage + '; private diagnostics withheld'); process.exitCode = 1; }
  finally { fs.writeFileSync(artifact('mfa.json'), JSON.stringify(report, null, 2)); await browser.close(); }
  if (!process.exitCode) console.log('Published HTTPS MFA browser journey and ten accessibility/layout scans passed.');
})();
