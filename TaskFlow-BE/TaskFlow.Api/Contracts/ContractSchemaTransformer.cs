using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using TaskFlow.Api.Controllers;

namespace TaskFlow.Api.Contracts;

public sealed class ContractSchemaTransformer : IOpenApiSchemaTransformer
{
    public const string IdPattern = "^0*[1-9][0-9]*$";
    public const string TimestampPattern = @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$";
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var type = context.JsonTypeInfo.Type;
        if (type.IsEnum) { schema.Type = JsonSchemaType.String; SetEnum(schema, Enum.GetNames(type)); }
        if (type == typeof(int) || type == typeof(long))
        { schema.Type = JsonSchemaType.Integer; schema.Pattern = null; }
        if (schema.Properties is not { Count: > 0 }) return Task.CompletedTask;
        bool response = type.Name.EndsWith("Dto") || type.Name.EndsWith("Page")
            || type.Name == "TaskStatusCounts" || type == typeof(HealthResponse) || type.Name == "ProjectMemberList";
        if (response) schema.Required = schema.Properties.Keys.ToHashSet();
        if (type == typeof(ApiProblem)) schema.Required = new HashSet<string> { "type", "title", "status", "traceId" };
        if (type.Name.EndsWith("Request")) schema.AdditionalPropertiesAllowed = false;
        if (type == typeof(TaskPatchRequest))
        {
            schema.Required = new HashSet<string>();
            schema.MinProperties = 1;
            schema.Description = "At least one editable field. Omission preserves the current value; null clears only description, assigneeId and dueDateUtc. Text is trimmed. Unknown/duplicate properties are rejected.";
        }
        if (type == typeof(AddProjectMemberRequest)) schema.Required = new HashSet<string> { "userId" };
        if (type == typeof(TaskStatusRequest)) schema.Required = new HashSet<string> { "status" };
        foreach (var (name, value) in schema.Properties)
        {
            if (value is not OpenApiSchema property) continue;
            if (type == typeof(AddProjectMemberRequest) && name == "role") { SetEnum(property, ["Member"]); property.Default = JsonValue.Create("Member"); }
            if (type == typeof(CreateTaskRequest) && name == "priority") property.Default = JsonValue.Create("Medium");
            if (name == "email") property.Format = "email";
            if (schema.Required?.Contains(name) == true && property.Type == JsonSchemaType.String && property.MinLength is null or 0) property.MinLength = 1;
            if (name is "id" or "projectId" or "ownerId" or "userId" or "assigneeId" or "actorUserId" or "taskId" or "workspaceId" or "targetUserId")
            {
                property.Pattern = IdPattern;
                property.Description = "Positive signed BIGINT as an opaque decimal string (maximum numeric value 9223372036854775807). Inputs may include leading zeros; responses use canonical decimal. Never convert to a JavaScript number.";
            }
            if (name is "dueDateUtc" or "fromUtc" or "toUtc")
            {
                property.Format = "date-time"; property.Pattern = TimestampPattern;
                property.Description = "ISO timestamp with Z or explicit offset, UTC years 1000-9999. Task deadlines stored at microsecond precision; responses use UTC.";
            }
            if (type == typeof(TaskPatchRequest) && name == "priority") { property.Type = JsonSchemaType.String; SetEnum(property, ["Low", "Medium", "High"]); }
            if (type == typeof(TaskStatusRequest) && name == "status") { property.Type = JsonSchemaType.String; SetEnum(property, ["ToDo", "InProgress", "Done"]); }
            if (response && name == "status" && type.Name.StartsWith("Task")) SetEnum(property, ["ToDo", "InProgress", "Done"]);
            if (response && name == "priority") SetEnum(property, ["Low", "Medium", "High"]);
            if (response && name == "role") SetEnum(property, type.Name is "WorkspaceDto" or "WorkspaceMemberDto" ? ["Owner", "Admin", "Member", "Guest"] : ["Owner", "Member", "Editor", "Viewer"]);
            if (type == typeof(WorkspaceProjectMemberRequest) && name == "role") { SetEnum(property, ["Member", "Editor", "Viewer"]); property.Default = JsonValue.Create("Member"); }
            if (type == typeof(WorkspaceProjectRoleRequest) && name == "role") SetEnum(property, ["Editor", "Viewer"]);
            if (type == typeof(WorkspaceRoleRequest) && name == "role") SetEnum(property, ["Owner", "Admin", "Member", "Guest"]);
            if (type == typeof(WorkspaceStateRequest) && name == "state" || type.Name == "WorkspaceMemberDto" && name == "state") SetEnum(property, ["Active", "Suspended", "Removed"]);
            if (type.Name == "WorkspaceMemberDto" && name is "role" or "state") property.Enum?.Add(null!);
            if (name == "password") { property.Format = "password"; property.WriteOnly = true; }
            if (type == typeof(HealthResponse) && name == "status") SetEnum(property, ["Healthy", "Unhealthy", "Degraded"]);
        }
        return Task.CompletedTask;
    }
    public static void SetEnum(OpenApiSchema schema, string[] values) => schema.Enum = values.Select(v => (JsonNode)JsonValue.Create(v)!).ToList();
}
