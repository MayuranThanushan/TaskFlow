# API foundation

Delivery step 04 is complete. No database connection, authentication, or business resources are implemented by this step.

## Run and inspect

Start the API with the root README's HTTP command (localhost:5140), then start Vite in TaskFlow-FE (localhost:5173). Vite forwards `/api` to the API with no path rewriting. Browser requests should use relative `/api/v1/...` URLs. Same-origin proxying avoids a local cross-origin policy; no permissive CORS policy is registered.

For a different API address, copy TaskFlow-FE/.env.example to TaskFlow-FE/.env.local and set TASKFLOW_API_TARGET. This is a Vite server setting, not a VITE_ variable shipped to browsers. Restart Vite after changing it. Port 5173 is strict so an occupied port produces an error rather than silently selecting another origin.

```powershell
Invoke-RestMethod http://localhost:5173/api/v1/health/live
```

Expected: `{ "status": "Healthy" }`. The GET-only endpoint is anonymous and returns Cache-Control: no-store. It checks process liveness with no dependency checks. It must not be used to claim database readiness. A database readiness check belongs to step 05 or later, once real persistence exists.

OpenAPI remains available directly at `http://localhost:5140/openapi/v1.json` in Development and now documents liveness. It is not exposed by the production configuration. Example requests are in TaskFlow.Api.http.

The default local HTTP profile is for these foundation checks. Production enables HSTS/HTTPS redirection and requires correct host/proxy TLS configuration. Before cookie authentication is implemented in step 07, set up trusted HTTPS on the browser-facing local origin and use the HTTPS API target; Secure cookies must not be tested as though HTTP establishes a secure session. No TLS certificate verification bypass is configured.

## Errors and validation

AddApiFoundation registers controllers, JSON configuration, validation response handling, Problem Details, the global exception handler, health services and OpenAPI. Program.cs configures the middleware pipeline. Controller DTOs use DataAnnotations/[ApiController]; concrete business validation rules will arrive with those DTOs.

- Unknown body properties and integer enum values are rejected by JSON configuration.
- Invalid model state or malformed JSON returns 400 with field-level errors. Binding/parser exceptions are replaced with a generic message.
- Empty error statuses such as missing routes (404) and unsupported methods (405) return application/problem+json.
- Unhandled exceptions return safe 500 Problem Details in both Development and Production, even when the request accepts HTML. No exception message or stack trace is sent to the caller.
- Error responses include traceId; every response includes the matching X-Trace-Id header. General error responses use no-store. MVC validation responses contain the same trace metadata.

Example missing route response (trace value illustrative):

```json
{
  "type": "about:blank",
  "title": "Not Found",
  "status": 404,
  "traceId": "00-example"
}
```

Step 07 added global duplicate-key rejection, default controller authorization and antiforgery validation. Presence-aware PATCH, resource permission checks and business error mappings remain feature implementation work. Do not treat global JSON/model validation as fulfillment of those domain contracts.

## Logging and verification

Console logs are JSON with UTC timestamps and trace scopes. Request completion events include method, matched route pattern, status and elapsed milliseconds. They omit request bodies, headers and query strings. The custom exception handler records the exception type and trace identifier without logging its message. Keep secret values out of exception messages and future application logs as well.

ApiFoundationTests uses WebApplicationFactory with test-only probe controllers injected into the test host. No failure/probe controller is compiled into the API. The suite verifies health and OpenAPI, malformed/invalid/unknown fields, missing routes, wrong methods, safe failures in Development/Production, and trace consistency. Combined with the existing provider compatibility check, nine tests pass.

A separate local smoke check started API and Vite on temporary ports and verified both a successful health request and a 404 Problem Details response through the proxy. Both processes were stopped afterward. Frontend type-check/build/lint and backend build pass. No live MySQL verification occurred.

Implementation follows [ASP.NET Core error-handling guidance](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/error-handling?view=aspnetcore-10.0) and [Vite proxy configuration](https://vite.dev/config/server-options#server-proxy).

Step 07 account routes now require HTTPS and use default controller authorization plus global CSRF protection. Liveness remains explicitly public. The initial HTTP proxy configuration is for foundation checks; see [authentication](authentication.md) for the implemented HTTPS proxy configuration.

Step 09 adds database/schema readiness at `/api/v1/health/ready` and production static SPA hosting with preserved JSON API errors. See [deployment](deployment.md) for the published-app smoke check and remaining host/container gates.
