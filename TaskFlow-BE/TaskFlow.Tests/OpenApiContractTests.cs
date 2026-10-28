using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace TaskFlow.Tests;

public class OpenApiContractTests
{
    private static async Task<JsonNode> Live()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    public static string Canonical(JsonNode node)
    {
        JsonNode Sort(JsonNode? value) => value switch
        {
            JsonObject obj => new JsonObject(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key, p.Value is null ? null : Sort(p.Value)))),
            JsonArray array => new JsonArray(array.Select(v => v is null ? null : Sort(v)).ToArray()),
            _ => value!.DeepClone()
        };
        return Sort(node).ToJsonString();
    }

    [Fact]
    public async Task SnapshotMatchesLiveContract()
    {
        var live = await Live();
        if (Environment.GetEnvironmentVariable("TASKFLOW_OPENAPI_EXPORT") is { Length: > 0 } output)
        {
            await File.WriteAllTextAsync(output, live.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
            return; // Explicit regeneration mode; normal CI never sets this variable.
        }
        var snapshot = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Contracts/openapi.json")))!;
        Assert.True(Canonical(snapshot) == Canonical(live), "OpenAPI drift: explicitly regenerate docs/contracts/openapi.json, review the diff and regenerate frontend types. See docs/contracts/README.md.");
    }

    [Fact]
    public void SemanticDriftIsDetectedButObjectOrderingIsIgnored()
    {
        var original = JsonNode.Parse("{\"paths\":{\"/tasks\":{\"get\":{\"responses\":{\"200\":{\"type\":\"string\"}}}}},\"openapi\":\"3.1.1\"}")!;
        var reordered = JsonNode.Parse("{\"openapi\":\"3.1.1\",\"paths\":{\"/tasks\":{\"get\":{\"responses\":{\"200\":{\"type\":\"string\"}}}}}}")!;
        Assert.Equal(Canonical(original), Canonical(reordered));
        reordered["paths"]!["/tasks"]!["get"]!["responses"]!["200"]!["type"] = "number";
        Assert.NotEqual(Canonical(original), Canonical(reordered));
    }

    [Fact]
    public async Task SchemasPreserveOpaqueIdsPatchPresenceEnumsAndQueryDefaults()
    {
        var live = await Live();
        var schemas = live["components"]!["schemas"]!;
        var patch = schemas["TaskPatchRequest"]!;
        Assert.Equal(1, patch["minProperties"]!.GetValue<int>());
        Assert.False(patch["additionalProperties"]!.GetValue<bool>());
        Assert.True(patch["required"] is null || patch["required"]!.AsArray().Count == 0);
        Assert.Equal("string", patch["properties"]!["title"]!["type"]!.GetValue<string>());
        Assert.Equal("string", schemas["TaskPriority"]!["type"]!.GetValue<string>());
        Assert.Equal(new[] { "Low", "Medium", "High" }, schemas["TaskPriority"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal(new[] { "ToDo", "InProgress", "Done" }, schemas["TaskStatusRequest"]!["properties"]!["status"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("string", schemas["TaskDto"]!["properties"]!["id"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", schemas["TaskStatusCounts"]!["properties"]!["done"]!["type"]!.GetValue<string>());
        Assert.Null(schemas["ApiProblem"]!["properties"]!["traceId"]!["pattern"]);
        Assert.Contains("description", schemas["TaskDto"]!["required"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Contains("null", schemas["TaskDto"]!["properties"]!["description"]!["type"]!.AsArray().Select(v => v!.GetValue<string>()));
        var member = schemas["AddProjectMemberRequest"]!;
        Assert.DoesNotContain("role", member["required"]!.AsArray().Select(v => v!.GetValue<string>()));
        var query = live["paths"]!["/api/v1/projects"]!["get"]!["parameters"]!.AsArray().ToDictionary(p => p!["name"]!.GetValue<string>());
        Assert.Equal(new[] { "page", "pageSize", "search", "sortBy", "sortDirection" }, query.Keys);
        Assert.Equal(20, query["pageSize"]!["schema"]!["default"]!.GetValue<int>());
        Assert.Equal("createdAtUtc", query["sortBy"]!["schema"]!["default"]!.GetValue<string>());
        Assert.Equal(new[] { "name", "createdAtUtc", "updatedAtUtc" }, query["sortBy"]!["schema"]!["enum"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Null(schemas["JsonElement"]);
    }

    [Fact]
    public async Task EveryOperationDocumentsSuccessAndSessionCsrfErrors()
    {
        var live = await Live();
        var paths = live["paths"]!.AsObject();
        Assert.Equal(29, paths.Count);
        var operations = paths.SelectMany(p => p.Value!.AsObject().Where(o => new[] { "get", "post", "put", "patch", "delete" }.Contains(o.Key)).Select(o => (Path: p.Key, Method: o.Key, Operation: o.Value!))).ToList();
        Assert.Equal(44, operations.Count);
        Assert.Equal(44, operations.Select(o => o.Operation["operationId"]!.GetValue<string>()).Distinct().Count());
        foreach (var (path, method, operation) in operations)
        {
            var success = operation["responses"]!.AsObject().Where(r => r.Key.StartsWith('2')).ToList();
            Assert.Single(success);
            if (success[0].Key != "204") Assert.NotNull(success[0].Value!["content"]);
            Assert.NotNull(operation["responses"]!["500"]!["content"]!["application/problem+json"]);
            bool anonymous = path.StartsWith("/api/v1/health/") || path is "/api/v1/auth/login" or "/api/v1/auth/register" or "/api/v1/auth/csrf";
            bool write = method is "post" or "put" or "patch" or "delete";
            var security = operation["security"]!.AsArray();
            Assert.Equal(!anonymous || write ? 2 : 0, security.Count);
            foreach (var requirement in security)
            {
                Assert.Equal(!anonymous, requirement!.AsObject().Any(p => p.Key.StartsWith("Session")));
                Assert.Equal(write, requirement.AsObject().ContainsKey("CsrfHeader"));
                Assert.Equal(write, requirement.AsObject().Any(p => p.Key.StartsWith("CsrfCookie")));
            }
            if (write) Assert.Contains(operation["parameters"]!.AsArray(), p => p!["name"]!.GetValue<string>() == "X-CSRF-TOKEN" && p["required"]!.GetValue<bool>());
        }
    }
}
