# Frontend authentication - step 08

The React application provides `/`, `/register`, `/login`, protected `/dashboard`, and an unknown-route recovery page. Use [HTTPS setup](authentication.md#local-https-setup) with MySQL running; account endpoints reject HTTP. Vite proxies `/api` to the API. Production must serve the SPA and API under one HTTPS origin and provide history fallback for frontend deep links; step 09 configures the selected host.

## Screens and session behavior

Registration validates name, email and password, trims name/email only, and preserves password characters. Success returns to sign-in with the email filled and password cleared; registration never signs in automatically. Forms provide associated labels/errors, password-manager autocomplete, pending/disabled states and a focused error summary. Duplicate email, invalid credentials, throttling and connection failures have usable messages.

The session provider checks `/auth/me` before rendering guest or protected screens. It restores the cookie session after reload without storing credentials or tokens in browser storage. Unauthorized protected requests and failed session rechecks redirect to sign-in with an expiry notice. Focus, visibility and a one-minute visible-page interval recheck signed-in sessions. Temporary connection/server failures retain the current authenticated view with a retry banner; initial failures show a retry screen. Request generations prevent older session checks and unauthorized responses from replacing a newer session.

Protected visits preserve the internal destination, including query and fragment, through registration/sign-in. External, protocol-relative, backslash and account-page return destinations fall back to `/dashboard`. Signed-in users visiting account forms return to the workspace. Logout succeeds only after the server confirms it (or says the session is already absent); server/network failure retains the signed-in view and offers another attempt. Backend authorization remains authoritative.

The shell has a keyboard skip link, route titles, navigation focus, responsive layouts and current-user navigation. The workspace is an empty foundation for later project/task features.

## API boundary

`src/api/client.ts` centralizes Axios, the fixed `/api/v1` base, credentialed cookies, a 15-second timeout and normalized errors. Shared account models live in `src/api/types.ts`; auth UI/session/routes live in `src/features/auth`.

CSRF bootstrap requests are shared while pending. Request tokens stay in memory and attach to unsafe methods. Login/logout invalidate cached identity-bound tokens and prefetch fresh ones. Only an explicit 400 validation response containing `errors.csrf` permits one retry after token refresh. Network errors, 500s and ordinary validation failures never automatically replay writes. An old CSRF bootstrap cannot overwrite the token cache after an identity transition. Session cookies stay HttpOnly and are managed exclusively by the browser/server.

## Verification

Twenty-four frontend tests cover API token sharing/refresh, one-retry limits, write failure behavior, origin restrictions, stale unauthorized responses, identity transitions, protected loading/redirects, registration, invalid fields, safe login errors, rate limiting, duplicate-submit prevention, restoration, expiry, connection recovery and logout failure/success. Tests use Axios adapters and Testing Library under StrictMode. Type checking, ESLint, Vitest/JUnit and production build pass. Existing CI automatically runs these tests.

A local headless Edge check used the real HTTPS Vite proxy, API and MySQL: protected deep link, registration, failed login, successful login, original query/fragment, reload, logout and reload. It checked Secure/HttpOnly/SameSite cookies, empty local/session storage, no page errors and no horizontal overflow at 390px. Desktop and mobile screenshots were inspected. The browser was pinned to the local development certificate public key for this check; API/proxy certificate verification remained enabled. No machine trust configuration changed.

The backend suite also passed all 30 tests with live MySQL and no skips. The separate HTTPS protocol check retained a session across API restart using persisted protection keys. Temporary services stopped and synthetic accounts were removed afterward. Local browser drivers/screenshots remain ignored verification artifacts, not CI dependencies; committed browser end-to-end automation remains later backlog work. No hosted CI run or deployment is claimed.
