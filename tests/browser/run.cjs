const { spawnSync } = require('node:child_process');
const path = require('node:path');
const { artifact } = require('./support.cjs');
const fs = require('node:fs');
const results = [];
for (const name of ['PROJECT_OWNER_EMAIL', 'PROJECT_OWNER_PASSWORD', 'BROWSER_TEST_EMAIL'])
  if (!process.env[name]) throw Error('Missing browser verification setting: ' + name);
for (const script of ['authentication.cjs', 'journeys.cjs', 'profile.cjs', 'review.cjs']) {
  const started = Date.now();
  const result = spawnSync(process.execPath, [path.join(__dirname, script)], { stdio: 'inherit', timeout: 240_000 });
  results.push({ script, passed: result.status === 0, durationMs: Date.now() - started });
  fs.writeFileSync(artifact('journeys.json'), JSON.stringify(results, null, 2));
  if (result.status !== 0) process.exit(1);
}
