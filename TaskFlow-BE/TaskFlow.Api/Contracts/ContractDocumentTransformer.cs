using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace TaskFlow.Api.Contracts;

public sealed class ContractDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Info.Title = "TaskFlow API";
        document.Info.Version = "v1+v2-read-preview";
        document.Info.Description = "Implemented v1 project-based MVP plus opt-in read-only workspace v2 preview (WorkspaceReads:Enabled, default false). Workspace writes, UI cutover and expectedVersion remain pending. Session cookies are HttpOnly; CSRF requires cookie plus header. Local versus Strict security alternatives describe deployment modes, not interchangeable cookies accepted by one deployment. Transport:RequireHttps selects cookie names; public HTTPS is the hosted default. Unknown/repeated collection query parameters are rejected case-insensitively; Enums and dynamic task PATCH field names are case-sensitive; typed body binding accepts name casing variants.";
        document.Servers = [new OpenApiServer { Url = "/" }];
        document.Components ??= new OpenApiComponents();
        document.Components.Schemas?.Remove(nameof(System.Text.Json.JsonElement));
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>();
        foreach (var mode in new[] { "Local", "Strict" })
        {
            var prefix = mode == "Strict" ? "__Host-TaskFlow." : "TaskFlow.";
            document.Components.SecuritySchemes["Session" + mode] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = prefix + "Auth", Description = "HttpOnly session cookie; sent by browser, never stored as a bearer token. Strict requires HTTPS; Local supports configured HTTP/HTTPS." };
            document.Components.SecuritySchemes["CsrfCookie" + mode] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = prefix + "Csrf", Description = "HttpOnly antiforgery cookie issued by GET /api/v1/auth/csrf; must match the request token and current identity." };
        }
        document.Components.SecuritySchemes["CsrfHeader"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-CSRF-TOKEN" };
        return Task.CompletedTask;
    }
}
