"""Bounded, synthetic, real-API benchmark. Requires an explicitly disposable HTTPS host."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import copy
from datetime import datetime, timezone
import hashlib
import http.cookiejar
import json
import math
import os
import platform
import secrets
from pathlib import Path
import ssl
import subprocess
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def origin_url(value, allow_remote=False):
    value = value.rstrip('/')
    parsed = urllib.parse.urlsplit(value)
    if (parsed.scheme != 'https' or not parsed.hostname or parsed.username is not None
            or parsed.password is not None or parsed.path or parsed.query or parsed.fragment):
        raise ValueError('Use an HTTPS origin without credentials, path, query or fragment')
    if not allow_remote and parsed.hostname not in ('localhost', '127.0.0.1', '::1'):
        raise ValueError('Remote targets require --allow-remote and an isolated disposable environment')
    return value


def validate_config(config):
    bounds = {'groups': (2, 10), 'usersPerGroup': (2, 25), 'projectsPerGroup': (1, 20),
              'tasksPerProject': (2, 1000), 'concurrency': (1, 50), 'requests': (8, 100000),
              'warmupRequestsPerWorker': (8, 100), 'timeoutSeconds': (1, 60)}
    if config.get('version') != 1:
        raise ValueError('Unsupported load fixture version')
    for key, (low, high) in bounds.items():
        value = config.get(key)
        if type(value) is not int or not low <= value <= high:
            raise ValueError('Invalid bounded fixture parameter: ' + key)
    if config['requests'] < config['concurrency'] * 8:
        raise ValueError('Require at least one full route mix per worker')
    if config['concurrency'] < config['groups']:
        raise ValueError('Concurrency must exercise every project group')
    budgets = config['proposedBudgets']
    for key in ('readP95Milliseconds', 'writeP95Milliseconds', 'unexpectedResponseRate'):
        value = budgets.get(key)
        if type(value) not in (int, float) or not math.isfinite(value) or value <= 0:
            raise ValueError('Invalid proposed budget: ' + key)
    if budgets['unexpectedResponseRate'] >= 1:
        raise ValueError('Unexpected response budget must be below one')
    return config


class Client:
    def __init__(self, origin, context, timeout, cookies=None):
        self.origin, self.context, self.timeout = origin, context, timeout
        self.jar = http.cookiejar.CookieJar()
        if cookies is not None:
            for cookie in cookies:
                self.jar.set_cookie(copy.copy(cookie))
        self.opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(),
            urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = None

    def request(self, path, method='GET', data=None):
        headers = {'Accept': 'application/json'}
        body = None
        if data is not None:
            headers['Content-Type'] = 'application/json'
            body = json.dumps(data).encode()
        if method != 'GET' and self.csrf:
            headers['X-CSRF-TOKEN'] = self.csrf
        request = urllib.request.Request(self.origin + '/api/v1' + path, data=body, headers=headers, method=method)
        try:
            response = self.opener.open(request, timeout=self.timeout)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            raw = response.read(2_000_001)
            if len(raw) > 2_000_000:
                raise ValueError('Response exceeded benchmark size limit')
            return response.status, json.loads(raw) if raw else None

    def expect(self, path, method='GET', data=None, status=200):
        actual, payload = self.request(path, method, data)
        if actual != status:
            # Deliberately omit URL, body, account data and exception messages.
            raise ValueError('Fixture/preflight request failed with HTTP ' + str(actual))
        return payload

    def token(self):
        self.csrf = self.expect('/auth/csrf')['requestToken']

    def clone(self):
        client = Client(self.origin, self.context, self.timeout, self.jar)
        client.token()
        return client


def seed(origin, context, config):
    groups = []
    run_id = uuid.uuid4().hex
    password = secrets.token_urlsafe(24) + 'aA1!'
    for group_index in range(config['groups']):
        clients = []
        for user_index in range(config['usersPerGroup']):
            client = Client(origin, context, config['timeoutSeconds'])
            client.token()
            credentials = {'email': f'load-{run_id}-{group_index}-{user_index}@example.test', 'password': password}
            client.expect('/auth/register', 'POST', {'displayName': 'Synthetic load user', **credentials}, 201)
            client.expect('/auth/login', 'POST', credentials)
            client.token()  # Antiforgery token must reflect the authenticated identity.
            identity = client.expect('/auth/me')
            clients.append((client, identity['id']))
        owner = clients[0][0]
        projects = []
        for project_index in range(config['projectsPerGroup']):
            project = owner.expect('/projects', 'POST', {'name': f'Load project {project_index}', 'description': 'Synthetic benchmark'}, 201)
            path = '/projects/' + project['id']
            for _, member_id in clients[1:]:
                owner.expect(path + '/members', 'POST', {'userId': member_id}, 201)
            tasks = []
            for task_index in range(config['tasksPerProject']):
                task = owner.expect(path + '/tasks', 'POST', {
                    'title': f'Load task {task_index}', 'priority': ['Low', 'Medium', 'High'][task_index % 3],
                    'assigneeId': clients[task_index % len(clients)][1],
                    'dueDateUtc': '2026-01-01T12:00:00Z' if task_index % 2 else None}, 201)
                if task_index % 3:
                    owner.expect('/tasks/' + task['id'] + '/status', 'PATCH', {'status': 'Done' if task_index % 3 == 2 else 'InProgress'})
                tasks.append(task['id'])
            projects.append({'id': project['id'], 'tasks': tasks})
        groups.append({'clients': clients, 'projects': projects})
    for index, group in enumerate(groups):
        other = groups[(index + 1) % len(groups)]['projects'][0]
        for client, _ in group['clients']:
            if client.expect('/projects')['totalCount'] != config['projectsPerGroup']:
                raise ValueError('Fixture project visibility mismatch')
            client.expect('/projects/' + other['id'], status=404)
            client.expect('/tasks/' + other['tasks'][0], status=404)
    return groups


def operation(group, index):
    # A hot parent project intentionally exercises serialization among writers.
    project = group['projects'][0]
    path = '/projects/' + project['id']
    mix = [('/dashboard', 'GET', None, 'dashboard'),
           ('/projects?pageSize=20&sortBy=updatedAtUtc', 'GET', None, 'projects'),
           (path + '/tasks?pageSize=20&status=InProgress&priority=Medium&sortBy=dueDateUtc', 'GET', None, 'filtered_tasks'),
           (path + '/activity?pageSize=20', 'GET', None, 'activity'),
           (path + '/members', 'GET', None, 'members'),
           ('/tasks/' + project['tasks'][0], 'GET', None, 'task_detail'),
           ('/tasks/' + project['tasks'][index % len(project['tasks'])] + '/status', 'PATCH',
            {'status': 'InProgress' if (index // 8) % 2 else 'ToDo'}, 'task_status'),
           (path, 'GET', None, 'project_detail')]
    return mix[index % len(mix)]


def percentile(values, quantile):
    if not values:
        return None
    return round(sorted(values)[max(0, math.ceil(len(values) * quantile) - 1)], 3)


def summarize(samples):
    def aggregate(rows):
        return {'count': len(rows), 'p50Milliseconds': percentile([r[2] for r in rows], .5),
                'p95Milliseconds': percentile([r[2] for r in rows], .95),
                'p99Milliseconds': percentile([r[2] for r in rows], .99),
                'unexpected': sum(r[1] != 200 for r in rows),
                'statusCounts': {str(code): sum(r[1] == code for r in rows) for code in sorted({r[1] for r in rows})}}
    names = sorted({row[0] for row in samples})
    return {'all': aggregate(samples), 'byOperation': {name: aggregate([r for r in samples if r[0] == name]) for name in names}}


def budget_failures(summary, budgets):
    failures = []
    all_rows = summary['all']
    if not all_rows['count'] or all_rows['unexpected'] / all_rows['count'] > budgets['unexpectedResponseRate']:
        failures.append('unexpectedResponseRate')
    for name, rows in summary['byOperation'].items():
        key = 'writeP95Milliseconds' if name == 'task_status' else 'readP95Milliseconds'
        if rows['p95Milliseconds'] > budgets[key]:
            failures.append(name + '.p95')
    return failures


def db_snapshot(mysql, defaults_file):
    if not mysql and not defaults_file:
        return None
    if not mysql or not defaults_file:
        raise ValueError('Supply both MySQL executable and private defaults file')
    query = ("SHOW GLOBAL STATUS WHERE Variable_name IN ('Innodb_row_lock_waits','Innodb_row_lock_time'); "
             "SELECT NAME, COUNT FROM information_schema.INNODB_METRICS "
             "WHERE NAME = 'lock_deadlocks' AND STATUS = 'enabled'")
    result = subprocess.run([mysql, '--defaults-extra-file=' + str(Path(defaults_file).resolve()),
        '--batch', '--skip-column-names', '-e', query], capture_output=True, text=True, timeout=15)
    if result.returncode:
        raise ValueError('Database metric snapshot failed; private client output withheld')
    values = dict(line.split('\t') for line in result.stdout.splitlines() if line)
    required = {'Innodb_row_lock_waits', 'Innodb_row_lock_time', 'lock_deadlocks'}
    if set(values) != required:
        raise ValueError('Database metric snapshot was incomplete')
    return {key: int(value) for key, value in values.items()}


def run(args):
    if not args.disposable:
        raise ValueError('Require --disposable: this command creates synthetic users and data')
    origin = origin_url(args.origin, args.allow_remote)
    fixture_bytes = Path(args.fixture).read_bytes()
    config = validate_config(json.loads(fixture_bytes))
    context = ssl.create_default_context(cafile=args.ca_file)
    before_setup = time.perf_counter()
    groups = seed(origin, context, config)
    setup_seconds = time.perf_counter() - before_setup
    barrier = threading.Barrier(config['concurrency'] + 1, timeout=120)

    def worker(worker_index):
        group = groups[worker_index % len(groups)]
        client = group['clients'][(worker_index // len(groups)) % len(group['clients'])][0].clone()
        for i in range(config['warmupRequestsPerWorker']):
            path, method, data, _ = operation(group, i)
            client.expect(path, method, data)
        barrier.wait()
        rows = []
        for i in range(worker_index, config['requests'], config['concurrency']):
            # Each worker cycles the full mix, irrespective of concurrency.
            path, method, data, label = operation(group, i // config['concurrency'] + worker_index)
            start = time.perf_counter()
            try:
                status, _ = client.request(path, method, data)
            except (OSError, ValueError, TimeoutError):
                status = 0
            rows.append((label, status, (time.perf_counter() - start) * 1000))
        return rows

    with ThreadPoolExecutor(max_workers=config['concurrency']) as pool:
        futures = [pool.submit(worker, i) for i in range(config['concurrency'])]
        # Warm-up completes before DB/timing snapshots; failures break the barrier.
        try:
            while barrier.n_waiting < config['concurrency']:
                for future in futures:
                    if future.done():
                        future.result()
                time.sleep(.01)
            before = db_snapshot(args.mysql, args.mysql_defaults_file)
            start = time.perf_counter()
            barrier.wait()
        except BaseException:
            barrier.abort()
            raise
        samples = [row for future in futures for row in future.result()]
        elapsed = time.perf_counter() - start
        after = db_snapshot(args.mysql, args.mysql_defaults_file)
    summary = summarize(samples)
    failures = budget_failures(summary, config['proposedBudgets'])
    delta = None if before is None else {key: after[key] - value for key, value in before.items()}
    if delta is not None and any(value < 0 for value in delta.values()):
        raise ValueError('Database counters reset during measurement')
    report = {'version': 1, 'measuredAtUtc': datetime.now(timezone.utc).isoformat(),
        'scope': 'Closed-loop real API project-group benchmark; not workspace isolation or production capacity certification',
        'driver': {'python': platform.python_version(), 'os': platform.system(),
                   'logicalProcessors': os.cpu_count(),
                   'sourceSha256NormalizedLf': hashlib.sha256(Path(__file__).read_text(encoding='utf-8').encode()).hexdigest(),
                   'connectionModel': 'urllib HTTPS, new connection per request, TLS validation enabled, no automatic retries'},
        'fixture': config, 'fixtureSha256': hashlib.sha256(fixture_bytes).hexdigest(),
        'setupSeconds': round(setup_seconds, 3), 'measuredSeconds': round(elapsed, 3),
        'requestsPerSecond': round(len(samples) / elapsed, 3), 'summary': summary,
        'database': {'scope': 'Server-global cumulative counter deltas, may include unrelated workloads',
                     'measured': delta is not None, 'delta': delta},
        'proposedBudgetFailures': failures, 'budgetsEnforced': args.enforce_budgets,
        'cleanup': 'Synthetic accounts/data remain. Destroy only the dedicated disposable database through its lifecycle owner.'}
    target = Path(args.output)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'requests': len(samples), 'unexpected': summary['all']['unexpected'],
        'p95Milliseconds': summary['all']['p95Milliseconds'], 'proposedBudgetFailures': failures}))
    return 1 if summary['all']['unexpected'] or args.enforce_budgets and failures else 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('origin')
    parser.add_argument('--fixture', default='tests/load/local-baseline.json')
    parser.add_argument('--output', default='TestResults/load/baseline.json')
    parser.add_argument('--ca-file')
    parser.add_argument('--disposable', action='store_true')
    parser.add_argument('--allow-remote', action='store_true')
    parser.add_argument('--enforce-budgets', action='store_true')
    parser.add_argument('--mysql', help='MySQL client executable; optional, with defaults file')
    parser.add_argument('--mysql-defaults-file', help='Private connection options, never copied into report')
    args = parser.parse_args()
    try:
        return run(args)
    except Exception as error:
        # TLS/socket/client errors can include target or private values. Emit only safe class.
        print('Load verification failed (' + type(error).__name__ + '); check fixture, disposable host, TLS and private client configuration.')
        return 1


if __name__ == '__main__':
    raise SystemExit(main())
