# API contract handoff

[openapi.json](openapi.json) is the ASP.NET Core Development OpenAPI 3.1.1 document exported from the implemented API: 17 paths and 24 operations. The generated server URL is normalized to `/` for a portable same-origin contract; paths and schemas are unchanged. It is a route/schema snapshot, not a complete description of security, dynamic PATCH bodies or every error response. In particular, JsonElement request/response signatures do not describe every task/membership DTO. Read it with the normative [API amendments](api-baseline.md), [authentication](../authentication.md), [task mutations](../task-mutations.md) and feature documents linked in the [handoff](../handoff.md). No generated client is supplied.

All business routes require the session cookie except register/login/CSRF and public health checks. Every state-changing controller request requires the antiforgery cookie plus X-CSRF-TOKEN, including registration and login. IDs are decimal strings, not JavaScript numbers. Do not infer anonymous access from the snapshot's absence of security annotations. Production returns 404 for /openapi; it has no public Swagger UI.

With the Development API running using the [HTTPS quickstart](../../README.md):

```powershell
python scripts/export_openapi.py --url https://localhost:7258/openapi/v1.json --ca .local/dev-certs/localhost.pem
```

The exporter writes docs/contracts/openapi.json by default and validates its route/operation inventory before writing. It requires HTTPS, or loopback HTTP for local tooling, and accepts an optional public CA PEM without disabling TLS checks. Export only from your local Development host. Review the diff when controllers or DTOs change. Request examples, allowlists, pagination, PATCH omission/null semantics and 400/401/403/404/409/429 responses are maintained in [api-baseline.md](api-baseline.md).
