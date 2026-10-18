# Authentication and secure sessions

Delivery step 07 implements the account API. Step 08 adds [frontend account forms and navigation](frontend-authentication.md). No email verification, recovery, password/email editing, or third-party sign-in flow is included.

## API contract

All routes below are under `/api/v1/auth` and require HTTPS. HTTP requests are rejected with 400 Problem Details rather than redirected. Auth responses are no-store. Error responses include traceId and the matching X-Trace-Id header.

| Method / route | Input | Successful response |
| --- | --- | --- |
| GET /csrf | No body | 200 `{requestToken, headerName}` and secure antiforgery cookie |
| POST /register | displayName, email, password | 201 profile; Location /api/v1/auth/me; does not sign in |
| POST /login | email, password | 200 profile and a secure session cookie |
| GET /me | Session cookie | 200 current profile |
| POST /logout | Session cookie; no body | 204 after server-side session invalidation |

Profile is exactly `{id, displayName, email}`; ID is a decimal string. There is no access token, password hash, security stamp, role or verification flag in the response. Display name and email are trimmed; passwords are preserved exactly. Required limits are display name 100, email 256 and password 128 UTF-16 code units. Registration uses the agreed minimum-12 password policy with uppercase/lowercase/digit/non-alphanumeric requirements. Login accepts an incorrect shorter nonempty password and returns the same generic failure as other invalid credentials.

Duplicate normalized email returns 409, including database uniqueness races. Invalid data, unknown fields and duplicate JSON keys return 400. Validation keys use JSON property names. Invalid, unknown and locked-account sign-ins return generic 401 Problem Details with no login redirect. Unknown accounts perform a dummy password-hash verification to reduce the fast failure path; this is not a constant-time timing guarantee.

Controllers are authenticated by default through a global authorization filter. Liveness, CSRF bootstrap, registration and login explicitly allow anonymous callers. Resource-level owner/member authorization is enforced by the implemented project/task use cases.

## CSRF and session lifecycle

All state-changing controller requests use a global filter calling the framework IAntiforgery service. Supply the bootstrap requestToken in X-CSRF-TOKEN. Tokens are bound to the client's antiforgery cookie and authenticated identity. Missing, invalid, borrowed or stale-identity tokens return safe 400 validation problems. Fetch a fresh token after login and logout; keep it in application memory rather than localStorage.

The session cookie is `__Host-TaskFlow.Auth`, Secure, HttpOnly, SameSite=Lax, Path=/ with no Domain. It is not persistent across browser sessions and its server ticket expires after eight hours. There is no sliding renewal. The antiforgery cookie is similarly Secure/HttpOnly/SameSite=Lax with the __Host prefix.

Each authenticated request validates the Identity security stamp directly through SignInManager. This avoids periodically cached checks and avoids Identity's principal-renewal path, so revalidation cannot extend the ticket lifetime. Logout persists a new security stamp before clearing cookies, invalidating all of that account's device sessions on subsequent requests. A failed stamp write returns an error, not a successful logout. Requests already authorized before logout commits may finish.

Identity locks sign-in after five failed attempts for 15 minutes. Registration/login share a fixed-window limit of ten attempts per minute per remote IP, with no queue; excess requests return 429 and Retry-After. This is an in-process limiter for the current single-API architecture. Forwarded IP headers are not trusted automatically and cannot be used to change the limiter partition.

## Local HTTPS setup

Use the initialized MySQL instructions in [database setup](database-setup.md) first. The local portable server is stopped after verification and must be started when using the account API.

From the root, export the existing .NET localhost certificate with a generated local password:

```powershell
./scripts/Export-DevCertificate.ps1
```

The PFX, public PEM and export settings/password are placed under ignored .local/dev-certs. No certificate trust settings are changed by that script. To trust the development certificate in your own browser, the explicit machine setup command is `dotnet dev-certs https --trust`.

API terminal, from the root:

```powershell
$taskflowCert = Get-Content .local/dev-certs/settings.json -Raw | ConvertFrom-Json
$taskflowDb = Get-Content .local/mysql/connections.json -Raw | ConvertFrom-Json
$env:ConnectionStrings__TaskFlow = $taskflowDb.development
$env:ASPNETCORE_Kestrel__Certificates__Default__Path = $taskflowCert.pfxPath
$env:ASPNETCORE_Kestrel__Certificates__Default__Password = $taskflowCert.password
dotnet run --project TaskFlow-BE/TaskFlow.Api --launch-profile https
```

Frontend terminal, initially from the root:

```powershell
$taskflowCert = Get-Content .local/dev-certs/settings.json -Raw | ConvertFrom-Json
$env:TASKFLOW_API_TARGET = 'https://localhost:7258'
$env:TASKFLOW_HTTPS_PFX = $taskflowCert.pfxPath
$env:TASKFLOW_HTTPS_PASSWORD = $taskflowCert.password
$env:TASKFLOW_API_CA = $taskflowCert.certificatePath
cd TaskFlow-FE
npm run dev
```

The origin is now https://localhost:5173. Vite uses the exported certificate and validates the API certificate with the public PEM; no certificate-validation bypass is configured. These TASKFLOW_ settings are Vite server settings, not VITE_ browser variables. The existing HTTP profile still supports foundation/liveness checks, but cannot establish an authenticated session.

Data-protection keys persist under ignored .local/data-protection by default. Set DataProtection__KeysPath to an access-controlled persistent directory for hosting, with appropriate at-rest protection for the host. Ephemeral containers need a persistent key volume. For TLS-terminating proxies, configure a known-proxy forwarding policy during deployment or use HTTPS to the API; accepting arbitrary forwarded scheme/IP headers is intentionally not enabled here.

## Verification

The live backend suite contains 30 tests and passed with no skips. Auth checks cover registration/profile shape, cookie flags, no automatic registration sign-in, duplicate email, JSON validation, exact password handling, generic failures, lockout, CSRF binding/freshness, default authorization, 401/403 without redirects, rate limiting despite spoofed forwarding headers, expiry without renewal, all-device logout, replay rejection and failed stamp persistence.

A separate real-TLS check exercised registration/login/me through Vite's HTTPS proxy, restarted the API with persisted data-protection keys and verified the existing session, then refreshed CSRF and logged out. TLS certificate checks stayed enabled on both hops. Synthetic accounts were removed; API, Vite and MySQL processes were stopped afterward. This protocol check is supplemented by the step 08 [real Edge account flow](frontend-authentication.md#verification).

Frontend type-check/lint/24 tests/build pass. The migration snapshot is checked separately; no schema change is needed for these endpoints. GitHub CI uses the same live backend tests; no hosted workflow run is claimed.

Implementation references: [ASP.NET Core antiforgery](https://learn.microsoft.com/aspnet/core/security/anti-request-forgery?view=aspnetcore-10.0), [rate limiting](https://learn.microsoft.com/en-us/aspnet/core/performance/rate-limit?view=aspnetcore-10.0), and [Identity stamp validation](https://source.dot.net/Microsoft.AspNetCore.Identity/SecurityStampValidator.cs.html).

Step 20 adds authenticated, CSRF-protected `PATCH /api/v1/users/me` for display-name editing with the same ProfileDto. Email/ID/credentials are read-only and updates do not renew authentication tickets. See [profile editing](profile.md) for the contract, UI and verification.
