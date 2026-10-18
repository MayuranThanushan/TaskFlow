# Environment and HTTP/HTTPS transport

Transport:RequireHttps controls authentication transport, cookies, redirects and HSTS together. Set it before starting the API; restart to change it. No client API URL is hardcoded: the frontend uses relative /api requests and Vite proxies them in development.

| Environment (ASPNETCORE_ENVIRONMENT) | Default | Override |
| --- | --- | --- |
| Development / Local | HTTP and HTTPS | Transport__RequireHttps=true for HTTPS only |
| UAT / Staging / Production / other | HTTPS only | Transport__RequireHttps=false for HTTP and HTTPS |

Local/Development needs no setting to use HTTP. In two terminals with the database connection configured for the API:

```powershell
# Repository root, API terminal
dotnet run --project TaskFlow-BE/TaskFlow.Api --launch-profile http
```

```powershell
# Repository root, frontend terminal
$env:TASKFLOW_API_TARGET = 'http://localhost:5140'
npm --prefix TaskFlow-FE run dev
```

Open http://localhost:5173. Unset TASKFLOW_HTTPS_PFX in that terminal if you previously configured Vite HTTPS. For HTTPS on both hops use the [certificate setup](authentication.md#local-https-setup).

For a published application in UAT or Production, set the environment and listeners explicitly. Example UAT HTTP listener, with ConnectionStrings__TaskFlow and persistent DataProtection__KeysPath already configured:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'UAT' # Use Production for production settings
$env:Transport__RequireHttps = 'false'
$env:ASPNETCORE_URLS = 'http://localhost:8080'
dotnet ./TaskFlow.Api.dll
```

Run that command in the published app directory. To listen on both protocols, use an ASPNETCORE_URLS value such as http://localhost:8080;https://localhost:8443 and configure a valid Kestrel certificate. Setting RequireHttps does not create HTTPS listeners or supply certificates. dotnet run launch profiles override environment/listener values; use --no-launch-profile when supplying them manually. The existing production Compose deployment remains an HTTPS-only listener by design; these overrides apply directly to the published API process.

In HTTP-compatible mode, auth and CSRF cookies are TaskFlow.Auth and TaskFlow.Csrf: HttpOnly, SameSite=Lax and Secure only when the request uses HTTPS. In HTTPS-only mode, the existing __Host-TaskFlow.Auth/Csrf cookies remain always Secure. Cookie names change between modes, so sign in again after switching. Use a consistent scheme for a session: browsers do not send an HTTPS Secure cookie back over HTTP. HTTPS sessions keep their Secure flag rather than being weakened to support switching schemes mid-session.

CSRF validation, authorization, stamp revocation, expiry, rate limiting and error contracts remain enforced in either mode. HTTP transmits credentials and sessions without encryption; use HTTPS for public traffic. Behind a proxy, keep the public and API schemes aligned; arbitrary X-Forwarded-Proto headers are intentionally not trusted. Existing browser HSTS state can continue upgrading a previously HTTPS-only hostname even after configuration changes; use the intended HTTP hostname/profile for local testing.

Verification covers Local/UAT/Production registration, CSRF-protected login, profile, logout and replay rejection over both HTTP and HTTPS, plus default/override behavior and spoofed-forwarding rejection. Existing strict Production redirects and secure-cookie tests also remain in the suite.

Final verification: Release build passed with zero warnings/errors; all 119 backend tests passed with live MySQL and zero skips, plus all 16 operations checks. Frontend application code and dependencies are unchanged. No migration is required.
