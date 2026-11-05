using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TaskFlow.Api.Controllers;

namespace TaskFlow.Api.Contracts;

public sealed class ContractOperationTransformer : IOpenApiOperationTransformer
{
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.Description.ActionDescriptor is not ControllerActionDescriptor action) return;
        operation.OperationId = action.ControllerName + "_" + action.ActionName;
        var controller = action.ControllerTypeInfo.AsType();
        bool health = controller == typeof(HealthController);
        bool anonymous = action.EndpointMetadata.OfType<IAllowAnonymous>().Any();
        bool write = context.Description.HttpMethod is "POST" or "PUT" or "PATCH" or "DELETE";
        operation.Security = [];
        if (!health && (!anonymous || write))
        {
            foreach (var mode in new[] { "Local", "Strict" })
            {
                var requirement = new OpenApiSecurityRequirement();
                if (!anonymous) requirement.Add(new OpenApiSecuritySchemeReference("Session" + mode, context.Document), []);
                if (write)
                {
                    requirement.Add(new OpenApiSecuritySchemeReference("CsrfCookie" + mode, context.Document), []);
                    requirement.Add(new OpenApiSecuritySchemeReference("CsrfHeader", context.Document), []);
                }
                operation.Security.Add(requirement);
            }
        }
        if (write)
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter { Name = "X-CSRF-TOKEN", In = ParameterLocation.Header, Required = true,
                Description = "Request token returned by GET /api/v1/auth/csrf. Requires its paired cookie. Refresh after login/logout.", Schema = new OpenApiSchema { Type = JsonSchemaType.String, MinLength = 1 } });
        }
        if (controller == typeof(TasksController) && action.ActionName is "Update" or "SetStatus"
            || controller == typeof(WorkspaceWritesController) && action.ActionName is "UpdateTask" or "Status")
        {
            var bodyType = action.ActionName is "Update" or "UpdateTask" ? typeof(TaskPatchRequest) : typeof(TaskStatusRequest);
            var schema = await context.GetOrCreateSchemaAsync(bodyType, null, cancellationToken);
            if (context.Document!.Components?.Schemas?.ContainsKey(bodyType.Name) != true) context.Document.AddComponent(bodyType.Name, schema);
            operation.RequestBody = new OpenApiRequestBody { Required = true, Content = new Dictionary<string, OpenApiMediaType>
                { ["application/json"] = new() { Schema = new OpenApiSchemaReference(bodyType.Name, context.Document) } } };
        }
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is not OpenApiParameter concreteParameter || parameter.Schema is not OpenApiSchema schema) continue;
            if (parameter.In == ParameterLocation.Path)
            { schema.Pattern = ContractSchemaTransformer.IdPattern; schema.Description = "Positive decimal-string ID within signed BIGINT range."; }
            if (parameter.In != ParameterLocation.Query) continue;
            concreteParameter.Required = false;
            var name = char.ToLowerInvariant(parameter.Name![0]) + parameter.Name[1..];
            concreteParameter.Name = name;
            if (name is "page" or "pageSize") schema.Default = JsonValue.Create(name == "page" ? 1 : 20);
            if (name == "sortBy")
            {
                ContractSchemaTransformer.SetEnum(schema, (controller == typeof(ProjectsController) || controller == typeof(WorkspaceReadsController) && action.ActionName == "Projects") ? ["name", "createdAtUtc", "updatedAtUtc"] : ["title", "priority", "dueDateUtc", "createdAtUtc"]);
                schema.Default = JsonValue.Create("createdAtUtc");
            }
            if (name == "sortDirection") { ContractSchemaTransformer.SetEnum(schema, ["asc", "desc"]); schema.Default = JsonValue.Create("desc"); }
            if (name == "status") ContractSchemaTransformer.SetEnum(schema, ["ToDo", "InProgress", "Done"]);
            if (name == "priority") ContractSchemaTransformer.SetEnum(schema, ["Low", "Medium", "High"]);
            if (name == "assigneeId") { schema.Pattern = ContractSchemaTransformer.IdPattern; schema.Description = "Positive decimal-string ID within signed BIGINT range; omit rather than supplying an empty filter."; }
            if (name is "fromUtc" or "toUtc") { schema.Format = "date-time"; schema.Pattern = ContractSchemaTransformer.TimestampPattern; }
        }
        operation.Description = (operation.Description ?? "") +
            " Responses include X-Trace-Id and Cache-Control: no-store. IDs remain decimal strings. " +
            (controller == typeof(ProjectsController) || controller == typeof(TasksController) || controller == typeof(ActivityController)
                ? "Collection queries reject unsupported/repeated parameters; filters combine with AND. Paging defaults to 1/20, maximum pageSize 100; offset must fit Int32. " : "") +
            (controller == typeof(WorkspaceReadsController) ? "Read-only workspace preview: requires WorkspaceReads:Enabled (default false); disabled routes return 404. Active workspace membership plus private-project access are required. No company-admin private-project override; production cutover remains pending. " : "") +
            (controller == typeof(WorkspaceWritesController) && action.ActionName == "CreateWorkspace" ? "Company creation preview: requires both WorkspaceReads:Enabled and WorkspaceWrites:Enabled (defaults false), a validated session and CSRF. Creates a new company and its active Owner membership atomically. Name is the only writable field; no query parameters or legacy-owner mappings. This does not grant access to existing companies. " : controller == typeof(WorkspaceWritesController) ? "Scoped write preview: requires both WorkspaceReads:Enabled and WorkspaceWrites:Enabled (defaults false). Disabled routes return 404. Scope and actor are server-derived; no query parameters are allowed. Active company membership and explicit project permissions are rechecked under transaction locks. Company administration grants no private-project override. " : "") +
            (write ? "Canonical JSON properties use camelCase. Typed body binding is case-insensitive; dynamic task PATCH fields are case-sensitive. Unknown/duplicate fields fail validation. Permission checks use current database membership. " : "") +
            (health ? "Readiness 503 uses the health DTO, not Problem Details." : "Inaccessible/deleted resources return 404; visible forbidden operations return 403 where applicable.");
        if (controller == typeof(WorkspaceLifecycleController) || controller == typeof(WorkspaceAuditConsoleController) || controller == typeof(WorkspacePolicyController)) operation.Description += " Lifecycle preview requires all three WorkspaceReads/WorkspaceWrites/WorkspaceLifecycle flags. Enabling lifecycle closes legacy v1 resource APIs. Owner invariants, active membership and private-project ownership are rechecked under the workspace gate. Ownership changes require current-password proof; company audit contains stable IDs and no private project titles/content.";
        if (controller == typeof(WorkspaceAuditConsoleController)) operation.Description += " Company Owner/Admin only; project activity additionally requires an unrevoked project grant, including deleted projects. Filters combine with AND. Page size is at most 100 and offset at most 10000. Export tickets are protected, actor/company-bound and expire after five minutes; download repeats current authorization and regenerates at most 1000 records below the captured event ID watermark, with an explicit truncation flag. No security records, raw Details or private titles are exported. Export JSON is returned through authenticated CSRF-protected POST; tickets are not URLs or authority grants.";
        bool versionedWrite = controller == typeof(ProjectsController) && action.ActionName is "Update" or "Delete"
            || controller == typeof(TasksController) && action.ActionName is "Update" or "SetStatus" or "Delete"
            || controller == typeof(WorkspaceWritesController) && action.ActionName is "UpdateProject" or "DeleteProject" or "UpdateTask" or "Status" or "DeleteTask";
        if (versionedWrite)
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter { Name = "X-Expected-Version", In = ParameterLocation.Header,
                Required = controller == typeof(WorkspaceWritesController), Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[1-9][0-9]{0,18}$" },
                Description = "Revision returned by the loaded record, as a positive canonical decimal string within signed BIGINT. Scoped writes require it (428 if missing); legacy v1 accepts it optionally during cutover. Checked after authorization under the transaction gate. Stale revision returns 409 resource.conflict; no mutation or success audit commits. Never fetch a fresh revision automatically before retrying a draft." });
        }
        if (controller == typeof(AccountSecurityController)) operation.Description += " Account-security mail preview is default off and restricted to Development/Staging. Email requests return the same empty 202 for absent, unverified, cooling-down and accepted accounts. Tokens are submitted in JSON, expire within one hour and are never returned by this API. Confirmation/reset rotate the security stamp and reject replay; password change signs out all sessions. Reauthentication is password-proven, five-minute and bound to the session stamp. No real email transport is enabled.";
        if (controller == typeof(WorkspacePolicyController)) operation.Description += " Policy and aggregate capacity require active Owner/Admin membership; only Owner can update, with current-password proof and expectedVersion. Fixed pilot quotas are server-controlled. Retention periods are review settings; automatic purge is false. Disabling company audit exports is checked again on ticket creation/download. Query parameters and unknown body fields are rejected.";
        if (controller == typeof(InvitationsController)) operation.Description += " Requires all workspace rollout flags and Invitations:Enabled (default false). Company Owner/Admin can invite Member/Guest; Editor/Viewer invitations require both company Owner/Admin and current private-project ownership. The API never returns raw tokens or token hashes. Intended verified recipient and current inviter authority are checked under the workspace gate. Pending invitations reserve company seats; acceptance consumes a one-use SHA-256 token and commits grants/audit atomically. Resend rotates the token without extending expiry, requires expectedVersion, one-minute cooldown and at most three resends. No UI or real mail provider is enabled by this foundation.";
        var failures = new HashSet<int> { 500 };
        if (controller == typeof(InvitationsController)) { failures.Add(403); failures.Add(404); failures.Add(409); }
        if (controller == typeof(WorkspaceWritesController) && action.ActionName is "CreateWorkspace" or "CreateProject" or "CreateTask") failures.Add(409);
        if (versionedWrite) { failures.Add(409); if (controller == typeof(WorkspaceWritesController)) failures.Add(428); }
        if (!health)
        {
            failures.Add(400);
            if (controller == typeof(AccountSecurityController) && action.ActionName is "RequestVerification" or "ForgotPassword" or "ConfirmEmail" or "ResetPassword") failures.Add(503);
            if (action.EndpointMetadata.OfType<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>().Any()) failures.Add(429);
            if (controller == typeof(WorkspaceLifecycleController) || controller == typeof(WorkspaceAuditConsoleController) || controller == typeof(WorkspacePolicyController)) { failures.Add(403); failures.Add(404); failures.Add(409); }
            if (!anonymous || action.ActionName == "Login") failures.Add(401);
            if (controller == typeof(ProjectsController) && action.ActionName is "Get" or "Update" or "Delete"
                || controller == typeof(TasksController) || controller == typeof(ProjectMembersController) || controller == typeof(ActivityController) || controller == typeof(WorkspaceReadsController) || controller == typeof(WorkspaceWritesController)) failures.Add(404);
            if (controller == typeof(ProjectsController) && action.ActionName is "Update" or "Delete"
                || controller == typeof(TasksController) && action.ActionName == "Delete"
                || controller == typeof(ProjectMembersController) && action.ActionName != "List") failures.Add(403);
            if (controller == typeof(WorkspaceWritesController)) { failures.Add(403); if (action.ActionName is "AddMember" or "RemoveMember") failures.Add(409); }
            if (controller == typeof(UsersController) || controller == typeof(AuthController) && action.ActionName == "Register"
                || controller == typeof(ProjectMembersController) && action.ActionName != "List") failures.Add(409);
            if (controller == typeof(AuthController) && action.ActionName is "Login" or "Register") failures.Add(429);
            if (operation.RequestBody is not null) failures.Add(415);
        }
        var errorSchema = await context.GetOrCreateSchemaAsync(typeof(ApiProblem), null, cancellationToken);
        if (context.Document!.Components?.Schemas?.ContainsKey(nameof(ApiProblem)) != true) context.Document.AddComponent(nameof(ApiProblem), errorSchema);
        operation.Responses ??= new OpenApiResponses();
        foreach (var status in failures)
            operation.Responses[status.ToString()] = new OpenApiResponse { Description = Microsoft.AspNetCore.WebUtilities.ReasonPhrases.GetReasonPhrase(status),
                Content = new Dictionary<string, OpenApiMediaType> { ["application/problem+json"] = new() { Schema = new OpenApiSchemaReference(nameof(ApiProblem), context.Document) } } };
        foreach (var (code, response) in operation.Responses)
        {
            if (response is not OpenApiResponse concrete) continue;
            if (concrete.Content?.ContainsKey("application/json") == true)
                foreach (var media in concrete.Content.Keys.Where(k => k != "application/json").ToArray()) concrete.Content.Remove(media);
            concrete.Headers ??= new Dictionary<string, IOpenApiHeader>();
            concrete.Headers["X-Trace-Id"] = new OpenApiHeader { Description = "Correlation ID; matches error traceId.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            concrete.Headers["Cache-Control"] = new OpenApiHeader { Schema = new OpenApiSchema { Type = JsonSchemaType.String, Description = "Includes no-store; may also include no-cache." } };
            if (code == "201") concrete.Headers["Location"] = new OpenApiHeader { Description = "Relative resource URL; registration requires subsequent sign-in.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
            if (code == "429") concrete.Headers["Retry-After"] = new OpenApiHeader { Description = "Optional retry interval in seconds.", Schema = new OpenApiSchema { Type = JsonSchemaType.String } };
        }
    }
}
