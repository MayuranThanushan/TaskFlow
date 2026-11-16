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
            || controller == typeof(WorkspaceWritesController) && action.ActionName is "UpdateTask" or "Status" or "MoveTask")
        {
            var bodyType = action.ActionName == "MoveTask" ? typeof(TaskMoveRequest) : action.ActionName is "Update" or "UpdateTask" ? typeof(TaskPatchRequest) : typeof(TaskStatusRequest);
            var schema = await context.GetOrCreateSchemaAsync(bodyType, null, cancellationToken);
            if (context.Document!.Components?.Schemas?.ContainsKey(bodyType.Name) != true) context.Document.AddComponent(bodyType.Name, schema);
            operation.RequestBody = new OpenApiRequestBody { Required = true, Content = new Dictionary<string, OpenApiMediaType>
                { ["application/json"] = new() { Schema = new OpenApiSchemaReference(bodyType.Name, context.Document) } } };
        }
        foreach (var parameter in operation.Parameters ?? [])
        {
            if (parameter is not OpenApiParameter concreteParameter || parameter.Schema is not OpenApiSchema schema) continue;
            if (parameter.In == ParameterLocation.Path)
            {
                if (controller == typeof(WorkspaceReadsController) && action.ActionName == "BoardColumn" && parameter.Name == "status")
                { ContractSchemaTransformer.SetEnum(schema, ["ToDo", "InProgress", "Done"]); schema.Description = "Canonical task status; each column is paged independently."; }
                else { schema.Pattern = ContractSchemaTransformer.IdPattern; schema.Description = "Positive decimal-string ID within signed BIGINT range."; }
            }
            if (parameter.In != ParameterLocation.Query) continue;
            concreteParameter.Required = controller == typeof(CommentsController) && action.ActionName == "Candidates" && concreteParameter.Name == "search";
            var name = char.ToLowerInvariant(parameter.Name![0]) + parameter.Name[1..];
            concreteParameter.Name = name;
            if (name is "page" or "pageSize") schema.Default = JsonValue.Create(name == "page" ? 1 : 20);
            if (name == "sortBy")
            {
                ContractSchemaTransformer.SetEnum(schema, (controller == typeof(ProjectsController) || controller == typeof(WorkspaceReadsController) && action.ActionName == "Projects") ? ["name", "createdAtUtc", "updatedAtUtc"] : ["title", "priority", "dueDateUtc", "createdAtUtc", "rank"]);
                if(controller==typeof(WorkspaceReadsController)&&action.ActionName=="Work")ContractSchemaTransformer.SetEnum(schema,["dueDateUtc","createdAtUtc","title"]);
                schema.Default = JsonValue.Create(controller==typeof(WorkspaceReadsController)&&action.ActionName=="Work"?"dueDateUtc":controller == typeof(WorkspaceReadsController) && action.ActionName == "BoardColumn" ? "rank" : "createdAtUtc");
            }
            if (name == "sortDirection") { ContractSchemaTransformer.SetEnum(schema, ["asc", "desc"]); schema.Default = JsonValue.Create(controller == typeof(WorkspaceReadsController) && action.ActionName is "BoardColumn" or "Work" ? "asc" : "desc"); }
            if(name=="assignment"){ContractSchemaTransformer.SetEnum(schema,["mine","unassigned","all"]);schema.Default=JsonValue.Create("mine");}
            if(name=="queue"){ContractSchemaTransformer.SetEnum(schema,["open","due","overdue","all"]);schema.Default=JsonValue.Create("open");}
            if(name=="projectId"){schema.Pattern=ContractSchemaTransformer.IdPattern;schema.Description="Optional currently visible project ID; inaccessible projects return 404.";}
            if (name == "status") ContractSchemaTransformer.SetEnum(schema, ["ToDo", "InProgress", "Done"]);
            if (name == "priority") ContractSchemaTransformer.SetEnum(schema, ["Low", "Medium", "High"]);
            if (name == "assigneeId") { schema.Pattern = ContractSchemaTransformer.IdPattern; schema.Description = "Positive decimal-string ID within signed BIGINT range; omit rather than supplying an empty filter."; }
            if (name is "fromUtc" or "toUtc") { schema.Format = "date-time"; schema.Pattern = ContractSchemaTransformer.TimestampPattern; }
        }
        if (controller == typeof(WorkspaceReadsController) && action.ActionName is "Board" or "BoardColumn")
            operation.Description += " Board read model: exact list visibility and AND filters. Counts include all matching tasks, with all three canonical statuses including empty columns. Column paging defaults to 1/20, max 100; server-owned rank asc with ID desc tie-break by default. Each response is a read snapshot; pages across requests are live and can shift after edits. Rank and atomic move commands are supported; board UI remains SaaS-032.";
        if(controller==typeof(WorkspaceReadsController)&&action.ActionName=="Work")operation.Description+=" Company-wide authorized work query; exact shared task visibility, no company-admin private-project override or per-project fan-out. Assignment mine(default)/unassigned/all; mine never includes unassigned. Queue open(default)/due/overdue/all. One captured microsecond UTC cutoff: overdue is unfinished due<asOfUtc; due is unfinished due>=asOfUtc and <=dueThroughUtc (seven days). Completed tasks excluded from due/overdue. Search matches visible task titles or parent project names; filters AND together. Inclusive explicit-offset UTC bounds round lower up/upper down to database precision and exclude null deadlines. Facet counts apply all non-queue filters in the same read snapshot. Counts and page share a snapshot; pages across requests remain live. Default deadline asc/null-last then ID desc; max pageSize100, offset Int32. No write/audit/outbox side effect.";
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
            || controller == typeof(WorkspaceWritesController) && action.ActionName is "UpdateProject" or "DeleteProject" or "UpdateTask" or "Status" or "DeleteTask" or "MoveTask";
        if (versionedWrite)
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter { Name = "X-Expected-Version", In = ParameterLocation.Header,
                Required = controller == typeof(WorkspaceWritesController), Schema = new OpenApiSchema { Type = JsonSchemaType.String, Pattern = "^[1-9][0-9]{0,18}$" },
                Description = "Revision returned by the loaded record, as a positive canonical decimal string within signed BIGINT. Scoped writes require it (428 if missing); legacy v1 accepts it optionally during cutover. Checked after authorization under the transaction gate. Stale revision returns 409 resource.conflict; no mutation or success audit commits. Never fetch a fresh revision automatically before retrying a draft." });
        }
        if (controller == typeof(WorkspaceWritesController) && action.ActionName == "MoveTask")
            operation.Description += " Atomic scoped move/reorder: requires X-Expected-Version for the moved task and captured versions for supplied previous/next adjacent target-column cards. Null endpoints denote true column boundaries, not a filtered page boundary. Stale revisions or nonadjacent positions return 409; foreign/missing neighbors return 404. Server chooses rank; rebalance touches at most 500 cards and increments changed card revisions. Status/rank/version/audit/outbox commit together; a no-op creates no history. Reload affected columns after a move/conflict; never silently replay with fetched revisions.";
        if (controller == typeof(AccountSecurityController)) operation.Description += " Account-security mail preview is default off and restricted to Development/Staging. Email requests return the same empty 202 for absent, unverified, cooling-down and accepted accounts. Tokens are submitted in JSON, expire within one hour and are never returned by this API. Confirmation/reset rotate the security stamp and reject replay; password change signs out all sessions. MFA accounts additionally require an authenticator code for password change/reauthentication; email-token password reset also requires an authenticator or unused recovery code and never disables MFA. Reauthentication is password-proven, five-minute and bound to the session stamp. No real email transport is enabled.";
        if (controller == typeof(WorkspacePolicyController)) operation.Description += " Policy and aggregate capacity require active Owner/Admin membership; only Owner can update, with current-password proof and expectedVersion. Fixed pilot quotas are server-controlled. Retention periods are review settings; automatic purge is false. Disabling company audit exports is checked again on ticket creation/download. Query parameters and unknown body fields are rejected.";
        if (controller == typeof(InvitationsController)) operation.Description += " Requires all workspace rollout flags and Invitations:Enabled (default false). Company Owner/Admin can invite Member/Guest; Editor/Viewer invitations require both company Owner/Admin and current private-project ownership. The API never returns raw tokens or token hashes. Intended verified recipient and current inviter authority are checked under the workspace gate. Pending invitations reserve company seats; acceptance consumes a one-use SHA-256 token and commits grants/audit atomically. Resend rotates the token without extending expiry, requires expectedVersion, one-minute cooldown and at most three resends. Creates and resends share a database-serialized 20/hour company send budget; exhaustion returns 429 invitation.send_limited with bounded Retry-After. ProviderAccepted means SendGrid API acceptance, not recipient delivery. Sending is default off and staged recipient allowlisting is required.";
        if (controller == typeof(NotificationsController)) operation.Description += " Requires all workspace rollout flags and Notifications:Enabled (default false). Recipient is derived from the current session, never from input. Assignment notifications materialize in the business/outbox transaction; self-actions and historical event replay are suppressed. Lists/counts recheck active company and private-project access and exclude deleted resources. Names are live authorized projections, not stored title snapshots. Page offsets are bounded to 10000; list totalCount and unreadCount share a repeatable-read snapshot. Read/unread is idempotent under the company gate; only read state is mutable. No email or provider delivery is implied.";
        var failures = new HashSet<int> { 500 };
        if (controller == typeof(TaskWatchersController))
        {
            failures.Add(404); if(write){failures.Add(409);failures.Add(403);}
            operation.Description += " Personal task watching requires all workspace flags, Notifications:Enabled and Notifications:WatchersEnabled. Actor comes from the session; no watcher list or count is exposed. Task visibility precedes binding and is rechecked under the company gate. Default false/revision 0; PUT isWatching/expectedVersion is idempotent and version protected. Read-only collaborators may manage their own subscription. At most 90 stored subscriptions per task; direct mentions take priority and total comment fanout stays within 100. Unwatch affects future updates and fences old queued subscription versions; delivered history remains authorized.";
        }
        if (controller == typeof(NotificationsController) && action.ActionName == "Groups") operation.Description += " Groups are computed by task from the same currently visible source rows, never cached private titles. Each group retains source notification IDs via taskId-filtered List; counts are source events, not groups. Explicit read/unread still targets selected source IDs. No group write marks unseen arrivals.";
        if (controller == typeof(NotificationsController) && action.ActionName == "List") operation.Description += " Optional taskId filters only this recipient's currently visible rows; unreadCount remains the all-task inbox badge. Inaccessible or foreign task filters yield no matching rows.";
        if (controller == typeof(NotificationsController) && action.ActionName is "Preferences" or "SetPreferences") operation.Description += " Watcher noise settings: watcherDeliveryMode Immediate/Digest/Off, quietStartHour and quietEndHour (both null or distinct 0-23). Supplying mode replaces noise settings; omitting it preserves them. Digest requires dailyDigestEnabled and uses the chosen timezone/hour, shifted past quiet hours. Direct mentions/assignments keep their own delivery rules. Muting in-app or watcher mode cancels pending watcher work. Deferred delivery rechecks subscription version, current access and intervening revocations, expires after seven days and requires the separate scheduler.";
        if (controller == typeof(CommentsController))
        {
            failures.Add(404); failures.Add(403); if (write) failures.Add(409);
            operation.Description += " Requires all workspace rollout flags and Comments:Enabled. Task visibility is checked before binding and within each transaction. Plain text only, 1-5000 characters; canonical @[userId] mentions resolve only active current-project collaborators (max 10). Candidate search requires 2-100 name characters and returns at most 10 names/IDs, never email. Comment pages use 20 items and page 1-51. Guests/viewers are read-only; only the author edits, author or project owner deletes. PUT/DELETE body expectedVersion rejects stale changes. Deletion clears the body, preserves tombstone and append-only revision metadata without historical text. Create/edit/delete and metadata-only outbox commit atomically; new non-self mentions notify only currently eligible unmuted recipients when notifications are enabled. Existing mention replay and unchanged edits do not notify again.";
        }
        if (controller == typeof(NotificationsController)) { failures.Add(404); if (write) { failures.Add(403); failures.Add(429); } }
        if (controller == typeof(NotificationsController) && action.ActionName == "SetPreferences") failures.Add(409);
        if (controller == typeof(NotificationsController) && action.ActionName == "MarkRead") operation.Description += " Bulk mark-read takes 1-100 distinct explicit notificationIds. All must be currently visible to the recipient or the whole transaction returns 404 unchanged. New arrivals outside the selection are not marked.";
        if (controller == typeof(NotificationsController) && action.ActionName is "Preferences" or "SetPreferences") operation.Description += " Preferences belong to the current recipient in this company. Absent rows default to enabled in-app/TaskAssigned and revision 0. PUT requires both booleans and expectedVersion; stale drafts return 409 without overwrite. Muting suppresses future materialization, preserves history and never replays old assignments when re-enabled. Scheduled reminders/digest task cards are opt-in; absent settings default off, UTC, 09:00. Supply dueReminderEnabled, dailyDigestEnabled, timeZoneId (IANA) and digestHour (0..23) together, or omit all four to preserve scheduling settings. A separately enabled scheduler is required. DeliveryStatus InAppDelivered means a committed inbox row, never email delivery. Task-assignment email is unavailable; account security and invitation email are unaffected.";
        if (controller == typeof(MfaController))
        {
            operation.Description += " Optional MFA enrollment requires Mfa:Enabled and verified email; existing enrolled accounts always require a second factor at sign-in even if enrollment is later disabled. Setup is session/stamp-bound for ten minutes. Authenticator secrets are encrypted; ten 128-bit recovery codes are returned only on confirmation/rotation and retained only as hashes. TOTP and recovery consumption are serialized and reject replay. Recovery sign-in does not grant privileged-action proof. Mutations require current password and applicable factor. Successful credential changes invalidate other sessions; disable invalidates all sessions.";
            if (write) failures.Add(429);
        }
        if (context.Description.RelativePath?.StartsWith("api/v2/workspaces", StringComparison.Ordinal) == true && write)
        { failures.Add(403); operation.Description += " Opt-in Mfa:RequireForCompanyAdministrators requires currently active company Owner/Admin actors to have authenticator proof younger than five minutes. Recovery-code sign-in cannot satisfy this policy; account security and authenticator restoration remain available."; }
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
