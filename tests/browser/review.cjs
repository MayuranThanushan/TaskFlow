const assert = require('node:assert/strict');
const fs = require('node:fs');
const { performance } = require('node:perf_hooks');
const { chromium } = require('playwright-core');
const { launch, origin, artifact, submitLogin } = require('./support.cjs');
(async () => {
  const browser = await launch(chromium);
  const report = { startedAtUtc: new Date().toISOString(), browser: '', accessibility: [], controlBoundaries: [], keyboard: [], security: [], performance: [], errors: [] };
  try {
    report.browser = browser.version();
    const context = await browser.newContext({ viewport: { width: 1440, height: 1000 } });
    const page = await context.newPage();
    page.on('pageerror', error => report.errors.push(error.message));
    const axe = require.resolve('axe-core/axe.min.js');
    await page.route(origin + '/__browser_review_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(axe, 'utf8') }));
    async function scan(name, width = 1440) {
      await page.setViewportSize({ width, height: 1000 });
      await page.addScriptTag({ url: origin + '/__browser_review_axe.js' });
      const result = await page.evaluate(async () => {
        const result = await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa', 'best-practice'] } });
        return { violations: result.violations.map(v => ({ id: v.id, impact: v.impact, nodes: v.nodes.map(n => ({ target: n.target, summary: n.failureSummary })) })), incomplete: result.incomplete.map(v => ({ id: v.id, count: v.nodes.length, nodes: v.nodes.map(n => ({ target: n.target, summary: n.failureSummary })) })), passes: result.passes.length };
      });
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
      const boundaries = await page.evaluate(() => {
        const luminance = color => {
          const channels = color.match(/[\d.]+/g).slice(0, 3).map(Number).map(v => v / 255).map(v => v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4);
          return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
        };
        const contrast = (a, b) => (Math.max(a, b) + .05) / (Math.min(a, b) + .05);
        return [...document.querySelectorAll('input, select, textarea')].filter(e => !e.disabled && e.getClientRects().length).map(e => {
          const style = getComputedStyle(e);
          const border = luminance(style.borderTopColor); const inside = luminance(style.backgroundColor);
          let parent = e.parentElement; while (parent && getComputedStyle(parent).backgroundColor === 'rgba(0, 0, 0, 0)') parent = parent.parentElement;
          const outside = luminance(parent ? getComputedStyle(parent).backgroundColor : 'rgb(247,248,244)');
          return { control: e.id, insideRatio: +contrast(border, inside).toFixed(2), outsideRatio: +contrast(border, outside).toFixed(2) };
        });
      });
      report.controlBoundaries.push({ name, width, controls: boundaries });
      report.accessibility.push({ name, width, overflow, ...result });
      fs.writeFileSync(artifact('review.json'), JSON.stringify(report, null, 2));
      await page.screenshot({ path: artifact(name + '-' + width + '.png'), fullPage: true });
      // Collect all findings before failing, to allow a complete review in one pass.
    }
    await page.goto(origin + '/'); await page.getByRole('heading', { name: /Keep your/ }).waitFor(); await scan('home'); await scan('home', 320);
    await page.goto(origin + '/login'); await page.getByRole('heading', { name: 'Sign in to TaskFlow' }).waitFor(); await scan('login'); await scan('login', 320);
    await page.keyboard.press('Tab'); assert.equal(await page.locator('.skip-link').evaluate(e => e === document.activeElement), true); await page.keyboard.press('Enter');
    assert.equal(await page.locator('#main-content').evaluate(e => e === document.activeElement), true); report.keyboard.push('Skip link reaches main');
    await page.getByRole('button', { name: 'Sign in', exact: true }).click(); assert.equal(await page.getByLabel('Email address').evaluate(e => e === document.activeElement), true);
    await scan('login-validation', 390); report.keyboard.push('Required email receives focus with associated error');
    await page.goto(origin + '/register'); await page.getByLabel('Your name').waitFor(); await scan('register'); await scan('register', 320);
    await page.getByRole('button', { name: 'Create account', exact: true }).click(); assert.equal(await page.getByLabel('Your name').evaluate(e => e === document.activeElement), true); await scan('register-validation', 390);
    // E2E-001: one newly registered browser user completes the whole core workflow.
    await page.getByLabel('Your name').fill('New journey user'); await page.getByLabel('Email address').fill('critical-' + process.env.PROJECT_OWNER_EMAIL); await page.getByLabel('Password', { exact: true }).fill('Synthetic-NewJourney-Pass123!');
    await page.getByRole('button', { name: 'Create account', exact: true }).click(); await page.getByText('Account created. Sign in to continue.').waitFor();
    await page.getByLabel('Password', { exact: true }).fill('Synthetic-NewJourney-Pass123!'); await submitLogin(page); await page.getByRole('heading', { name: 'Welcome back, New journey user.' }).waitFor();
    await page.getByRole('link', { name: 'Projects', exact: true }).click(); await page.getByRole('link', { name: 'New project', exact: true }).click(); await page.getByLabel('Project name').fill('New user journey'); await page.getByRole('button', { name: 'Create project', exact: true }).click(); await page.getByRole('heading', { name: 'New user journey', exact: true }).waitFor();
    await page.getByRole('link', { name: 'View tasks', exact: true }).click(); await page.getByRole('link', { name: 'New task', exact: true }).click(); await page.getByLabel('Task title').fill('First journey task'); await page.getByRole('button', { name: 'Create task', exact: true }).click(); await page.getByRole('heading', { name: 'First journey task', exact: true }).waitFor();
    await page.getByLabel('Task status', { exact: true }).selectOption('Done'); await page.getByRole('button', { name: 'Update status', exact: true }).click(); await page.getByText('Task status updated.', { exact: true }).waitFor(); await page.reload(); await page.getByLabel('Task status', { exact: true }).waitFor(); assert.equal(await page.getByLabel('Task status', { exact: true }).inputValue(), 'Done');
    await page.getByRole('link', { name: 'Overview', exact: true }).click(); await page.getByRole('heading', { name: 'Workspace totals', exact: true }).waitFor();
    const complete = await page.evaluate(async () => (await fetch('/api/v1/dashboard')).json()); assert.equal(complete.totalProjects, 1); assert.equal(complete.totalTasks, 1); assert.equal(complete.tasksByStatus.done, 1);
    await page.getByRole('button', { name: 'Sign out', exact: true }).click(); await page.getByRole('heading', { name: 'Sign in to TaskFlow' }).waitFor(); report.security.push('E2E-001: same newly registered UI account creates project/task, changes status, verifies dashboard, reloads and signs out');
    await page.goto(origin + '/login'); await page.getByLabel('Email address').waitFor();
    await page.getByLabel('Email address').fill(process.env.PROJECT_OWNER_EMAIL); await page.getByLabel('Password', { exact: true }).fill(process.env.PROJECT_OWNER_PASSWORD);
    await submitLogin(page); await page.getByRole('heading', { name: /Welcome back/ }).waitFor(); await scan('dashboard'); await scan('dashboard', 320);
    async function request(route, options = {}) {
      return page.evaluate(async ({ route, options }) => {
        const response = await fetch(route, { method: options.method || 'GET', credentials: 'same-origin', redirect: 'error', headers: { ...(options.data === undefined ? {} : { 'Content-Type': 'application/json' }), ...options.headers }, ...(options.data === undefined ? {} : { body: JSON.stringify(options.data) }) });
        const text = await response.text(); let body = null; try { body = JSON.parse(text) } catch { /* Non-JSON SPA/error pages carry no API DTO. */ }
        return { status: response.status, headers: Object.fromEntries(response.headers), body };
      }, { route, options });
    }
    const csrf = (await request('/api/v1/auth/csrf')).body.requestToken;
    async function api(method, route, data, expected = 200, extraHeaders = {}) {
      const response = await request('/api/v1' + route, { method, ...(data === undefined ? {} : { data }), headers: { 'X-CSRF-TOKEN': csrf, ...extraHeaders } });
      assert.equal(response.status, expected, method + ' ' + route);
      assert.match(response.headers['cache-control'] || '', /no-store/);
      return expected === 204 ? null : response.body;
    }
    const canary = 'TASKFLOW_REVIEW_PRIVATE_';
    const project = await api('POST', '/projects', { name: 'Review fixture', description: canary + 'BODY <img src=x onerror=alert(1)>' }, 201);
    const projectPath = '/projects/' + project.id;
    const tasks = [];
    for (let i = 0; i < 100; i++) tasks.push(await api('POST', projectPath + '/tasks', { title: 'Review task ' + i.toString().padStart(3, '0'), description: null, priority: i % 2 ? 'High' : 'Low', assigneeId: null, dueDateUtc: null }, 201));
    const taskPath = '/tasks/' + tasks[0].id;
    await page.getByRole('link', { name: 'Projects', exact: true }).click(); await page.getByRole('link', { name: 'Review fixture', exact: true }).waitFor();
    assert.equal(await page.locator('#main-content').evaluate(e => e === document.activeElement), true); report.keyboard.push('Route navigation focuses main'); await scan('projects'); await scan('projects', 320);
    await page.goto(origin + '/projects/new'); await page.getByLabel('Project name').waitFor(); await scan('project-form');
    await page.getByRole('button', { name: 'Create project', exact: true }).click(); assert.equal(await page.getByLabel('Project name').evaluate(e => e === document.activeElement), true); await scan('project-validation', 320);
    await page.goto(origin + projectPath); await page.getByRole('heading', { name: 'Review fixture', exact: true }).waitFor(); await page.locator('.member-list strong').first().waitFor(); await scan('project-detail'); await scan('project-detail', 320);
    assert.equal(await page.locator('.project-description img').count(), 0); report.security.push('Stored HTML remains text');
    await page.getByRole('button', { name: 'Delete project', exact: true }).click(); assert.equal(await page.getByRole('button', { name: 'Keep project', exact: true }).evaluate(e => e === document.activeElement), true); await scan('project-delete', 390);
    await page.keyboard.press('Escape'); await page.waitForFunction(() => document.activeElement?.textContent === 'Delete project'); report.keyboard.push('Delete cancellation has safe initial focus and restores trigger on Escape');
    await page.goto(origin + projectPath + '/edit'); await page.getByLabel('Project name').waitFor(); await scan('project-edit', 768);
    await page.goto(origin + projectPath + '/tasks'); await page.locator('.task-table .task-name').first().waitFor(); await scan('tasks'); await scan('tasks', 320);
    await page.goto(origin + projectPath + '/tasks/new'); await page.getByLabel('Task title').waitFor(); await scan('task-form'); await scan('task-form', 320);
    await page.goto(origin + taskPath); await page.getByRole('heading', { name: tasks[0].title, exact: true }).waitFor(); await scan('task-detail'); await scan('task-detail', 320);
    await page.goto(origin + taskPath + '/edit'); await page.getByLabel('Task title').waitFor(); await scan('task-edit', 768);
    await page.goto(origin + projectPath + '/activity'); await page.locator('.activity-list li').first().waitFor(); await scan('activity'); await scan('activity', 320);
    await page.goto(origin + '/profile'); await page.getByLabel('Display name').waitFor(); await scan('profile'); await scan('profile', 320);
    const input = page.getByLabel('Display name'); await input.focus(); assert.notEqual(await input.evaluate(e => getComputedStyle(e).outlineStyle), 'none'); report.keyboard.push('Focused inputs have a visible outline');
    // Expected-error recovery: intercept one read only; writes are never replayed.
    await page.route('**/api/v1/projects?*', route => route.fulfill({ status: 500, contentType: 'application/problem+json', body: '{"status":500,"title":"Unexpected failure"}' }), { times: 1 });
    await page.goto(origin + '/projects?search=Review'); await page.getByRole('alert').waitFor(); await scan('safe-error', 390); await page.getByRole('button', { name: 'Try again', exact: true }).click(); await page.getByRole('link', { name: 'Review fixture', exact: true }).waitFor(); report.keyboard.push('Safe API error recovers on explicit retry');
    const beforeDeniedWrite = await api('GET', taskPath);
    const beforeDeniedEvents = (await api('GET', projectPath + '/activity')).totalCount;
    const outsiderContext = await browser.newContext(); const outsiderPage = await outsiderContext.newPage();
    await outsiderPage.goto(origin + '/register'); await outsiderPage.getByLabel('Your name').fill('Review outsider'); await outsiderPage.getByLabel('Email address').fill('review-outsider-' + process.env.PROJECT_OWNER_EMAIL); await outsiderPage.getByLabel('Password', { exact: true }).fill('Synthetic-Other-Pass123!'); await outsiderPage.getByRole('button', { name: 'Create account', exact: true }).click(); await outsiderPage.getByText('Account created. Sign in to continue.').waitFor(); await outsiderPage.getByLabel('Password', { exact: true }).fill('Synthetic-Other-Pass123!'); await submitLogin(outsiderPage);
    await outsiderPage.getByRole('heading', { name: 'Welcome back, Review outsider.' }).waitFor();
    const outsiderWrite = await outsiderPage.evaluate(async taskPath => {
      const csrf = await (await fetch('/api/v1/auth/csrf')).json();
      return (await fetch('/api/v1' + taskPath, { method: 'PATCH', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': csrf.requestToken }, body: JSON.stringify({ title: 'Unauthorized replacement' }) })).status;
    }, taskPath);
    assert.equal(outsiderWrite, 404); assert.deepEqual(await api('GET', taskPath), beforeDeniedWrite); assert.equal((await api('GET', projectPath + '/activity')).totalCount, beforeDeniedEvents);
    await outsiderContext.close(); report.security.push('Outsider direct task write denied with unchanged owner DTO and activity count');
    const denied = await request('/api/v1/users/me', { method: 'PATCH', data: { displayName: 'Unsafe' } }); assert.equal(denied.status, 400);
    const cors = await request('/api/v1/auth/me');
    const foreign = await context.newPage();
    await foreign.route('https://untrusted.example/', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><title>Isolated cross-origin probe</title>' }));
    await foreign.goto('https://untrusted.example/');
    assert.equal(await foreign.evaluate(async origin => { try { await fetch(origin + '/api/v1/auth/me', { credentials: 'include' }); return false } catch { return true } }, origin), true);
    await foreign.close(); assert.equal(cors.headers['access-control-allow-origin'], undefined);
    const headers = (await request('/profile')).headers; assert.match(headers['content-security-policy'], /frame-ancestors 'none'/); assert.equal(headers['x-content-type-options'], 'nosniff'); if (!['localhost', '127.0.0.1', '[::1]'].includes(new URL(origin).hostname)) assert.match(headers['strict-transport-security'] || '', /max-age=/);
    report.security.push(headers['strict-transport-security'] ? 'HSTS header present' : 'Localhost excluded by framework HSTS defaults; selected-host verification remains required');
    assert.equal((await request('/openapi/v1.json')).status, 404);
    await api('GET', projectPath + '/tasks?search=' + canary + 'QUERY', undefined, 200, { 'X-Review-Canary': canary + 'HEADER' });
    report.security.push('CSRF required; no cross-origin credential grant; CSP framing/script restrictions; nosniff; Production OpenAPI hidden');
    const visibleTotals = await api('GET', '/dashboard');
    const samples = Number(process.env.TASKFLOW_BROWSER_SAMPLES || 20); assert.ok(Number.isInteger(samples) && samples >= 5 && samples <= 100);
    const operations = [ ['profile-read', 'GET', '/auth/me'], ['projects', 'GET', '/projects?pageSize=20'], ['project-detail', 'GET', projectPath], ['members', 'GET', projectPath + '/members'], ['task-filter-sort-page', 'GET', projectPath + '/tasks?search=Review&priority=High&sortBy=title&sortDirection=asc&pageSize=20'], ['task-detail', 'GET', taskPath], ['dashboard', 'GET', '/dashboard'], ['activity', 'GET', projectPath + '/activity?pageSize=20'], ['task-status', 'PATCH', taskPath + '/status', i => ({ status: i % 2 ? 'Done' : 'InProgress' })], ['project-edit', 'PUT', projectPath, i => ({ name: 'Review fixture ' + i, description: canary + 'BODY' })], ['profile-edit', 'PATCH', '/users/me', i => ({ displayName: 'Review user ' + i })] ];
    for (const [name, method, route, body] of operations) {
      for (let i = 0; i < 3; i++) await api(method, route, body?.(i));
      const times = [];
      for (let i = 0; i < samples; i++) { const start = performance.now(); await api(method, route, body?.(i)); times.push(performance.now() - start); }
      times.sort((a, b) => a - b); const percentile = p => +times[Math.ceil(times.length * p) - 1].toFixed(2);
      report.performance.push({ name, samples, p50Ms: percentile(.5), p95Ms: percentile(.95), maxMs: +times.at(-1).toFixed(2), underTwoSeconds: times.at(-1) < 2000 });
    }
    await api('DELETE', projectPath, undefined, 204);
    report.fixture = { fixtureActiveTasks: 100, visibleProjects: visibleTotals.totalProjects, visibleTasks: visibleTotals.totalTasks, warmupsPerOperation: 3, samplesPerOperation: samples, concurrency: 1, transport: 'local HTTPS round trip including response body; cold starts excluded' };
    fs.writeFileSync(artifact('review.json'), JSON.stringify(report, null, 2));
    const findings = report.accessibility.filter(v => v.violations.length || v.overflow);
    if (findings.length) { console.error(JSON.stringify(findings, null, 2)); throw Error('Accessibility/responsive findings require review.'); }
    assert.ok(report.controlBoundaries.every(scan => scan.controls.every(c => c.insideRatio >= 3 && c.outsideRatio >= 3)), 'Form boundaries must have at least 3:1 contrast');
    assert.equal(report.errors.length, 0); assert.ok(report.performance.every(p => p.underTwoSeconds), 'Demo API latency exceeds SRS target');
    console.log('Accessibility, keyboard, security and demo performance review passed; evidence in review.json.');
  } finally { fs.writeFileSync(artifact('review.json'), JSON.stringify(report, null, 2)); await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
