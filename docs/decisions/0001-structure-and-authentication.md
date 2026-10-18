# 0001: Structure and authentication

Status: adopted. Date: 18 October 2026.

## Context

The SAD requires a layered monolithic API and React frontend. The user has created TaskFlow-BE, TaskFlow-FE, and a root TaskFlow.slnx. The source documents allow either tokens or cookies but require a single coherent authentication design.

## Decision

Preserve the existing paths and references. Domain has no dependency on other TaskFlow projects; Application references Domain; Infrastructure implements Application interfaces; Api wires services. Keep the existing xUnit project until integration tests justify a separate project. Use feature folders as features arrive, without adding empty abstractions.

Retain React Router and the already installed Axios for a centralized API client. Use component state and context initially. This supersedes the earlier plan's suggested fetch client; adding a second HTTP library is unnecessary.

Use ASP.NET Core Identity with an ApplicationUser<long> as the sole user store. Use cookie authentication, one public origin, and `/api/v1` API routes. A reverse proxy or API static-file host serves the production frontend. A Vite development proxy forwards relative `/api` requests; it is not configured in this task.

Authentication cookie: `__Host-TaskFlow.Auth`, HttpOnly, Secure, SameSite=Lax, Path=/, no Domain. Use a non-persistent browser cookie with an eight-hour authentication ticket and no sliding expiration. Require HTTPS for authentication in development as well as production. The HTTP launch profile is only for current unauthenticated starter checks.

Protect POST/PUT/PATCH/DELETE using ASP.NET antiforgery, including register/login/logout. An anonymous `GET /api/v1/auth/csrf` returns a request token; the framework's antiforgery cookie is Secure/HttpOnly/SameSite=Lax. The client sends the token using `X-CSRF-TOKEN`. Refresh the request token after login/logout because its identity binding changes. Never persist the request token in localStorage; hold it in memory. Missing/invalid token returns safe 400 Problem Details.

Login returns a profile and cookie, not a bearer token. Return JSON 401/403 without login-page redirects. Derive identity only from validated authentication claims.

Use Identity security-stamp validation on every authenticated request (`ValidationInterval = TimeSpan.Zero`) for this small MVP. Logout updates the user's security stamp before clearing the cookie, invalidating all of that user's sessions on subsequent requests. This deliberate all-session logout behavior avoids a separate session table; ordinary cookie sign-out alone does not meet replay prevention. Test replay of an old cookie. An identity-store failure must not be reported as successful logout.

Password baseline: minimum 12 characters; require uppercase, lowercase, digit and non-alphanumeric characters; no password trimming. Identity hashes passwords. Lockout after five failed sign-ins for 15 minutes. Apply a named authentication rate limit of ten register/login attempts per minute per client IP with no queue (429 on excess). Trust forwarded client IP headers only from configured proxies. Registration returns 201 without signing in. Email confirmation/recovery and email/password changes are deferred; account email is not presented as verified.

Persist data-protection keys outside ephemeral deployment storage; configure restricted origins only if a deployment later requires cross-origin requests. No permissive production CORS.

## Consequences and verification

Cookie/CSRF behavior needs browser verification through the same origin. Checking the security stamp on every request adds a database lookup; acceptable for the demonstration workload. Logout signs out all devices. Test registration, generic invalid login, lockout, expiry, CSRF, profile retrieval, replay after logout, and safe 401/403 responses. No authentication code is introduced by this decision task.

## Step 07 implementation

The account API is now implemented. Password inputs are bounded to 128 UTF-16 code units in addition to the established registration policy. The cookie callback directly calls SignInManager.ValidateSecurityStampAsync on every request without issuing a renewed principal/ticket; this makes revocation immediate while preserving absolute expiry. A global authorization filter protects controllers by default, and a global CSRF filter uses IAntiforgery directly without adding MVC view services. Local certificates and data-protection keys stay outside source control. See [authentication](../authentication.md) for implementation evidence and HTTPS setup.
