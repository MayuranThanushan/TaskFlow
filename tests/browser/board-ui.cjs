// Isolated UI review only: every API request is mocked; never connects to a real account/database.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { chromium } = require('playwright-core');
const origin = process.env.TASKFLOW_BOARD_UI_ORIGIN;
if (!origin || new URL(origin).origin !== origin || new URL(origin).hostname !== '127.0.0.1' || process.env.TASKFLOW_BOARD_UI_MOCKS !== '1') throw Error('Use an explicit loopback preview origin and TASKFLOW_BOARD_UI_MOCKS=1.');
const output = path.resolve(process.env.TASKFLOW_BOARD_UI_OUTPUT || path.join(__dirname, '../../TestResults/saas032-browser'));
fs.mkdirSync(output, { recursive: true });
const task = (id, title, status) => ({ id, projectId: '41', title, status, priority: 'Medium', assigneeId: null, description: null, dueDateUtc: null, version: '1', createdAtUtc: '2026-11-15T08:00:00Z', updatedAtUtc: '2026-11-15T08:00:00Z' });
(async () => {
  const state = { tasks: [task('101', 'Plan release', 'ToDo'), task('102', 'Review API', 'InProgress'), task('103', 'Ship notes', 'Done')], readonly: false, moveStatus: 200, writes: [] };
  const report = { scope: 'mocked API production frontend; no real accounts or database', checks: [], accessibility: [], errors: [] };
  const browser = await chromium.launch({ headless: true, ...(process.env.TASKFLOW_BROWSER_EXECUTABLE ? { executablePath: process.env.TASKFLOW_BROWSER_EXECUTABLE } : { channel: 'msedge' }) });
  let stage = 'start';
  try {
    const context = await browser.newContext({ viewport: { width: 1440, height: 1050 } }), page = await context.newPage();
    page.on('pageerror', () => report.errors.push('Browser script error; diagnostics withheld'));
    await context.route(origin + '/__board_axe.js', route => route.fulfill({ contentType: 'application/javascript', body: fs.readFileSync(require.resolve('axe-core/axe.min.js'), 'utf8') }));
    await context.route('**/api/**', async route => {
      const request = route.request(), url = new URL(request.url()), pathname = url.pathname, json = data => route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(data) });
      if (pathname === '/api/v1/auth/me') return json({ id: '1', displayName: 'Alex Morgan', email: 'synthetic@example.test' });
      if (pathname === '/api/v1/auth/csrf') return json({ requestToken: 'synthetic-paired-token' });
      if (pathname === '/api/v2/workspaces/1') return json({ id: '1', name: 'Product studio', role: state.readonly ? 'Member' : 'Owner' });
      const rows = status => state.tasks.filter(t => t.status === status);
      if (pathname.endsWith('/move')) {
        const move = request.postDataJSON(), source = state.tasks.find(t => t.id === pathname.split('/').at(-2));
        state.writes.push({ move, version: request.headers()['x-expected-version'] });
        if (state.moveStatus !== 200) return route.fulfill({ status: state.moveStatus, contentType: 'application/problem+json', body: JSON.stringify({ code: 'board.position_conflict', status: state.moveStatus }) });
        assert.equal(source.version, request.headers()['x-expected-version']);
        const destination = rows(move.status).filter(t => t.id !== source.id), at = move.nextTaskId ? destination.findIndex(t => t.id === move.nextTaskId) : destination.length;
        assert.equal(destination[at - 1]?.id ?? null, move.previousTaskId); assert.equal(destination[at]?.id ?? null, move.nextTaskId);
        state.tasks = state.tasks.filter(t => t.id !== source.id); source.status = move.status; source.version = String(Number(source.version) + 1);
        const next = move.nextTaskId ? state.tasks.findIndex(t => t.id === move.nextTaskId) : -1;
        if (next < 0) state.tasks.push(source); else state.tasks.splice(next, 0, source);
        return json(source);
      }
      if (pathname === '/api/v2/workspaces/1/projects/41') return json({ id: '41', ownerId: state.readonly ? '2' : '1', name: 'Quarterly product launch', description: null, version: '1', createdAtUtc: '2026-11-15T08:00:00Z', updatedAtUtc: '2026-11-15T08:00:00Z', totalTasks: state.tasks.length, completedTasks: rows('Done').length, progressPercentage: Math.round(rows('Done').length * 100 / state.tasks.length) });
      if (pathname.endsWith('/members')) return json({ items: [{ userId: '1', displayName: 'Alex Morgan', role: state.readonly ? 'Viewer' : 'Owner' }] });
      if (pathname.endsWith('/board')) return json({ projectId: '41', totalCount: state.tasks.length, columns: ['ToDo', 'InProgress', 'Done'].map(status => ({ status, totalCount: rows(status).length })) });
      if (pathname.includes('/columns/')) {
        const items = rows(pathname.split('/').at(-1)), number = Number(url.searchParams.get('page') || 1), size = Number(url.searchParams.get('pageSize') || 20);
        return json({ page: number, pageSize: size, totalCount: items.length, totalPages: Math.ceil(items.length / size), items: items.slice((number - 1) * size, number * size) });
      }
      return route.fulfill({ status: 404, contentType: 'application/problem+json', body: '{}' });
    });
    async function scan(name) {
      for (const width of [1440, 320]) {
        await page.setViewportSize({ width, height: 1050 }); await page.addScriptTag({ url: origin + '/__board_axe.js' });
        const result = await page.evaluate(async () => ({ violations: (await axe.run(document, { runOnly: { type: 'tag', values: ['wcag2a', 'wcag2aa', 'wcag21aa', 'wcag22aa', 'best-practice'] } })).violations.map(v => ({ id: v.id, targets: v.nodes.map(n => n.target) })), overflow: document.documentElement.scrollWidth > innerWidth }));
        report.accessibility.push({ name, width, ...result }); assert.equal(result.violations.length, 0); assert.equal(result.overflow, false);
        await page.screenshot({ path: path.join(output, name + '-' + width + '.png'), fullPage: true });
      }
      await page.setViewportSize({ width: 1440, height: 1050 });
    }
    async function prepared() { await page.getByRole('dialog').waitFor(); await page.waitForFunction(() => !document.getElementById('move-target-position').disabled); }
    async function saved() { await page.getByRole('dialog').waitFor({ state: 'hidden' }); await page.locator('.notice').filter({ hasText: 'Board refreshed' }).waitFor(); await page.waitForFunction(() => document.activeElement?.id === 'board-move-101'); assert.equal(await page.getByRole('button', { name: 'Move Plan release', exact: true }).evaluate(el => el === document.activeElement), true); }
    stage = 'owner'; await page.goto(origin + '/companies/1/projects/41/board'); await page.getByRole('link', { name: 'Plan release', exact: true }).waitFor(); await scan('board-owner');
    stage = 'dialog'; await page.getByRole('button', { name: 'Move Plan release', exact: true }).click(); await prepared();
    assert.equal(await page.getByLabel('Destination column').evaluate(el => el === document.activeElement), true, 'Destination select should receive initial focus');
    for (let i = 0; i < 7; i++) { await page.keyboard.press('Tab'); assert.equal(await page.getByRole('dialog').evaluate(el => el.contains(document.activeElement)), true, 'Focus escaped modal at Tab ' + i); }
    await scan('board-dialog'); await page.keyboard.press('Escape'); await page.getByRole('dialog').waitFor({ state: 'hidden' });
    assert.equal(await page.getByRole('button', { name: 'Move Plan release', exact: true }).evaluate(el => el === document.activeElement), true); assert.equal(state.writes.length, 0);
    report.checks.push('native modal focus containment, Escape cancellation and source focus restoration');
    stage = 'keyboard'; await page.getByRole('button', { name: 'Move Plan release', exact: true }).focus(); await page.keyboard.press('Alt+ArrowRight'); await prepared(); assert.equal(await page.getByLabel('Destination column').inputValue(), 'InProgress'); await page.getByRole('button', { name: 'Confirm move', exact: true }).click(); await saved();
    assert.equal(state.writes.length, 1); assert.equal(state.writes[0].version, '1'); await scan('board-empty-column');
    report.checks.push('keyboard move, explicit confirmation, committed count refresh and empty-column state');
    stage = 'drag-status'; await page.getByRole('button', { name: 'Move Plan release', exact: true }).dragTo(page.getByRole('heading', { name: 'Completed', exact: true })); await prepared(); assert.equal(await page.getByLabel('Destination column').inputValue(), 'Done'); await page.getByRole('button', { name: 'Confirm move', exact: true }).click(); await saved(); assert.equal(state.writes.length, 2);
    stage = 'drag-reorder'; const destination = page.getByRole('link', { name: 'Ship notes', exact: true }).locator('xpath=ancestor::li'); await page.getByRole('button', { name: 'Move Plan release', exact: true }).dragTo(destination); await prepared(); assert.equal(await page.getByLabel('Position in column').inputValue(), '103'); await page.getByRole('button', { name: 'Confirm move', exact: true }).click(); await saved();
    assert.equal(state.writes.length, 3); assert.equal(state.writes[2].move.previousTaskId, null); assert.equal(state.writes[2].move.nextTaskId, '103'); await scan('board-saved');
    report.checks.push('real pointer drag across columns and before-card reorder through captured-neighbor confirmation');
    stage = 'conflict'; state.moveStatus = 409; await page.getByRole('button', { name: 'Move Plan release', exact: true }).click(); await prepared(); await page.getByLabel('Destination column').selectOption('ToDo'); await prepared(); await page.getByRole('button', { name: 'Confirm move', exact: true }).click(); await page.getByRole('button', { name: 'Reload board to review', exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Confirm move', exact: true }).isDisabled(), true); assert.equal(state.tasks.find(t => t.id === '101').status, 'Done'); await scan('board-conflict');
    await page.getByRole('button', { name: 'Reload board to review', exact: true }).click(); await page.getByRole('dialog').waitFor({ state: 'hidden' }); assert.equal(state.writes.length, 4);
    report.checks.push('conflict rollback/review, no automatic POST replay and explicit reload');
    stage = 'viewer'; state.readonly = true; state.moveStatus = 200; await page.reload(); await page.getByRole('link', { name: 'Plan release', exact: true }).waitFor(); assert.equal(await page.getByRole('button', { name: 'Move Plan release', exact: true }).count(), 0); await scan('board-viewer');
    stage = 'pagination'; state.readonly = false; for (let i = 0; i < 125; i++) state.tasks.push(task(String(200 + i), 'Extra ' + String(i).padStart(3, '0'), 'ToDo')); state.tasks.find(t => t.id === '200').title = 'A long project task title ' + 'x'.repeat(150);
    await page.reload(); await page.getByRole('button', { name: 'Move Plan release', exact: true }).waitFor(); const todo = page.getByRole('region', { name: 'To do', exact: true }); await todo.getByRole('button', { name: 'Next', exact: true }).click(); await page.getByRole('link', { name: 'Extra 020', exact: true }).waitFor(); await scan('board-paged');
    report.checks.push('full 125-card column count, independent second page and long-title mobile wrapping');
    assert.equal(report.errors.length, 0);
    fs.writeFileSync(path.join(output, 'board-ui.json'), JSON.stringify({ ...report, passed: true }, null, 2));
    console.log(JSON.stringify({ checks: report.checks.length, accessibilityScans: report.accessibility.length, violations: 0, overflow: false, scriptErrors: 0 }));
  } catch (error) { fs.writeFileSync(path.join(output, 'board-ui-failure.json'), JSON.stringify({ ...report, stage, passed: false }, null, 2)); throw error; }
  finally { await browser.close(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
