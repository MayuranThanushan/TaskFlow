"""HTTPS deployment smoke check. Credentials come from TASKFLOW_SMOKE_EMAIL/PASSWORD.
Use --register only against a disposable verification environment.
"""
import argparse
import copy
import http.cookiejar
import json
import os
from pathlib import Path
import re
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('origin', help='HTTPS origin without a path')
    parser.add_argument('--ca-file', help='Public PEM CA/certificate for a private test origin')
    parser.add_argument('--register', action='store_true', help='Create the supplied disposable smoke account')
    phase = parser.add_mutually_exclusive_group()
    phase.add_argument('--keep-session', metavar='FILE', help='Save a sensitive session file for a restart check')
    phase.add_argument('--resume-session', metavar='FILE', help='Verify a saved session after restart, then log out')
    args = parser.parse_args()
    origin = args.origin.rstrip('/')
    parsed = urllib.parse.urlsplit(origin)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.path or parsed.query or parsed.fragment or parsed.username:
        parser.error('Supply an HTTPS origin without path, credentials, query or fragment.')
    if args.register and args.resume_session:
        parser.error('--register cannot be combined with --resume-session')
    context = ssl.create_default_context(cafile=args.ca_file)
    jar = http.cookiejar.LWPCookieJar()
    if args.resume_session:
        jar.load(args.resume_session, ignore_discard=True, ignore_expires=True)

    def opener(cookies):
        return urllib.request.build_opener(urllib.request.ProxyHandler({}), NoRedirect(),
            urllib.request.HTTPSHandler(context=context), urllib.request.HTTPCookieProcessor(cookies))

    browser = opener(jar)

    def request(path, body=None, token=None, method=None, client=browser):
        headers = {'Accept': 'application/json'}
        if body is not None:
            headers['Content-Type'] = 'application/json'
        if token:
            headers['X-CSRF-TOKEN'] = token
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(origin + path, data=data, headers=headers, method=method)
        try:
            response = client.open(req, timeout=20)
        except urllib.error.HTTPError as error:
            response = error
        with response:
            return response.status, response.headers, response.read()

    def require(condition, message):
        if not condition:
            raise RuntimeError(message)

    def profile(client=browser):
        status, _, body = request('/api/v1/auth/me', client=client)
        require(status == 200, 'Signed-in session was not accepted.')
        user = json.loads(body)
        require(isinstance(user.get('id'), str), 'Profile ID must be a string.')
        return user

    def token():
        status, _, body = request('/api/v1/auth/csrf')
        require(status == 200, 'CSRF bootstrap failed.')
        return json.loads(body)['requestToken']

    for path in ['/api/v1/health/live', '/api/v1/health/ready']:
        status, headers, body = request(path)
        require(status == 200 and json.loads(body)['status'] == 'Healthy', 'Health/readiness failed: ' + path)
        require('no-store' in headers.get('Cache-Control', ''), 'Health response must not be cached.')
    status, headers, html = request('/')
    require(status == 200 and 'text/html' in headers.get('Content-Type', ''), 'Published frontend is unavailable.')
    require('no-cache' in headers.get('Cache-Control', ''), 'Frontend entry must revalidate.')
    require("frame-ancestors 'none'" in headers.get('Content-Security-Policy', ''), 'Missing frontend security policy.')
    for path in ['/dashboard?smoke=1', '/login', '/register']:
        status, _, body = request(path)
        require(status == 200 and body == html, 'SPA deep link failed: ' + path)
    status, _, body = request('/dashboard', method='HEAD')
    require(status == 200 and not body, 'SPA HEAD response failed.')
    assets = re.findall(r'(?:src|href)="(/assets/[^"?#]+\.(?:js|css))"', html.decode())
    require(bool(assets), 'Compiled frontend assets were not found.')
    for path in assets:
        status, headers, body = request(path)
        require(status == 200 and bool(body), 'Compiled asset failed: ' + path)
        require('immutable' in headers.get('Cache-Control', ''), 'Hashed assets must use immutable caching.')
    for path in ['/api/v1/not-a-route', '/openapi/v1.json', '/assets/not-a-file.js']:
        status, headers, body = request(path)
        require(status == 404 and 'application/problem+json' in headers.get('Content-Type', ''), 'A server miss returned SPA HTML: ' + path)
        require(json.loads(body).get('traceId'), 'API problem lacks trace ID.')
    print('HTTPS, readiness, production assets, deep links, caching and JSON API routing passed.', flush=True)

    if args.resume_session:
        profile()
        print('Session survived application restart with persisted protection keys.', flush=True)
    else:
        email, password = os.environ.get('TASKFLOW_SMOKE_EMAIL'), os.environ.get('TASKFLOW_SMOKE_PASSWORD')
        require(bool(email and password), 'Set TASKFLOW_SMOKE_EMAIL and TASKFLOW_SMOKE_PASSWORD without putting secrets in arguments.')
        require(request('/api/v1/auth/me')[0] == 401, 'Anonymous current-user request must return 401.')
        csrf = token()
        credentials = {'email': email, 'password': password}
        if args.register:
            status, _, _ = request('/api/v1/auth/register', {'displayName': 'Deployment smoke', **credentials}, csrf)
            require(status == 201, 'Disposable account registration failed.')
            require(request('/api/v1/auth/me')[0] == 401, 'Registration must not sign in automatically.')
        require(request('/api/v1/auth/login', credentials)[0] == 400, 'A write without CSRF was accepted.')
        status, headers, _ = request('/api/v1/auth/login', credentials, csrf)
        require(status == 200, 'Smoke account login failed.')
        cookie = next((value for value in headers.get_all('Set-Cookie', []) if value.startswith('__Host-TaskFlow.Auth=')), '')
        require(all(part in cookie.lower() for part in ['secure', 'httponly', 'samesite=lax', 'path=/']), 'Authentication cookie flags are incorrect.')
        require('domain=' not in cookie.lower(), 'Host cookie must not specify Domain.')
        require(profile()['email'].casefold() == email.strip().casefold(), 'Login returned the wrong account.')
        print('CSRF rejection, login and secure cookie session passed.', flush=True)
        if args.keep_session:
            target = Path(args.keep_session)
            target.parent.mkdir(parents=True, exist_ok=True)
            # Exclusive creation avoids overwriting an existing sensitive artifact or following a symlink.
            descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
            os.close(descriptor)
            jar.save(str(target), ignore_discard=True, ignore_expires=True)
            print('Saved sensitive restart session; restrict access and remove it after verification.', flush=True)
            return

    replay_jar = http.cookiejar.CookieJar()
    for cookie in jar:
        replay_jar.set_cookie(copy.copy(cookie))
    replay = opener(replay_jar)
    require(request('/api/v1/auth/logout', method='POST')[0] == 400, 'Logout without CSRF was accepted.')
    require(request('/api/v1/auth/logout', {}, token())[0] == 204, 'Logout failed.')
    require(request('/api/v1/auth/me')[0] == 401, 'Logout did not clear the session.')
    require(request('/api/v1/auth/me', client=replay)[0] == 401, 'A revoked cookie was accepted after logout.')
    if args.resume_session:
        Path(args.resume_session).unlink()
    print('Logout and revoked-cookie replay rejection passed.', flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        # Do not dump response bodies, cookies, credentials or connection strings.
        print('Deployment smoke failed: ' + str(error), file=sys.stderr)
        sys.exit(1)
