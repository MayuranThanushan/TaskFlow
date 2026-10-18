const fs = require('node:fs');
const path = require('node:path');
const { X509Certificate, createHash } = require('node:crypto');
const origin = process.env.TASKFLOW_BROWSER_ORIGIN;
if (!origin || new URL(origin).origin !== origin || !origin.startsWith('https://')) throw Error('Set TASKFLOW_BROWSER_ORIGIN to an HTTPS origin without a path.');
if (process.env.TASKFLOW_BROWSER_DISPOSABLE !== '1') throw Error('This suite writes synthetic data. Set TASKFLOW_BROWSER_DISPOSABLE=1 only for an isolated verification database.');
const output = path.resolve(process.env.TASKFLOW_BROWSER_OUTPUT || path.join(__dirname, '../../TestResults/browser'));
fs.mkdirSync(output, { recursive: true });
const artifact = name => path.join(output, name);
async function launch(chromium) {
  const args = [];
  // Permit only the explicitly supplied private certificate; retain normal TLS checks otherwise.
  if (process.env.TASKFLOW_BROWSER_CA_FILE) {
    const certificate = new X509Certificate(fs.readFileSync(process.env.TASKFLOW_BROWSER_CA_FILE));
    const pin = createHash('sha256').update(certificate.publicKey.export({ type: 'spki', format: 'der' })).digest('base64');
    args.push('--ignore-certificate-errors-spki-list=' + pin);
  }
  return chromium.launch({ headless: true, ...(process.env.TASKFLOW_BROWSER_EXECUTABLE ? { executablePath: process.env.TASKFLOW_BROWSER_EXECUTABLE } : { channel: 'msedge' }), args });
}
async function submitLogin(page) {
  for (let attempt = 0; attempt < 2; attempt++) {
    const response = page.waitForResponse(r => r.url() === origin + '/api/v1/auth/login' && r.request().method() === 'POST');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click();
    const result = await response;
    if (result.status() !== 429) return;
    const seconds = Number(result.headers()['retry-after']);
    if (!Number.isFinite(seconds) || seconds < 0 || seconds > 60) throw Error('Unexpected auth retry interval');
    console.log('Authentication rate limit respected; waiting for Retry-After.');
    await page.waitForTimeout((seconds + 1) * 1000);
  }
  throw Error('Authentication remains rate limited');
}
module.exports = { origin, artifact, launch, submitLogin };
