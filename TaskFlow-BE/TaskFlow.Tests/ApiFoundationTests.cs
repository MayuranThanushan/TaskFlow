using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace TaskFlow.Tests;

public class ApiFoundationTests
{
    [Fact]
    public async Task LivenessReturnsHealthyWithoutDatabase()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var response = await client.GetAsync("/api/v1/health/live");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        Assert.True(response.Headers.Contains("X-Trace-Id"));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task DevelopmentOpenApiDocumentsLiveness()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));
        using var client = factory.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>("/openapi/v1.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/health/live", out _));
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ExceptionsReturnSafeProblemsInEveryEnvironment(string environment)
    {
        await using var factory = CreateProbeFactory(environment);
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html");
        var response = await client.GetAsync("/_tests/failure");
        var problem = await AssertProblem(response, HttpStatusCode.InternalServerError);
        Assert.DoesNotContain("secret-marker", problem.ToString());
        Assert.DoesNotContain("stackTrace", problem.ToString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{invalid")]
    [InlineData("{\"name\":\"okay\",\"ownerId\":\"123\"}")]
    public async Task InvalidBodiesReturnValidationProblems(string body)
    {
        await using var factory = CreateProbeFactory("Testing");
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        var token = await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token.GetProperty("requestToken").GetString());
        var response = await client.PostAsync("/_tests/validation", new StringContent(body, Encoding.UTF8, "application/json"));
        var problem = await AssertProblem(response, HttpStatusCode.BadRequest);
        Assert.True(problem.TryGetProperty("errors", out _));
    }

    [Fact]
    public async Task UnknownRoutesAndWrongMethodsReturnProblems()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        await AssertProblem(await client.GetAsync("/api/v1/missing"), HttpStatusCode.NotFound);
        await AssertProblem(await client.PostAsync("/api/v1/health/live", null), HttpStatusCode.MethodNotAllowed);
        await AssertProblem(await client.GetAsync("/_tests/failure"), HttpStatusCode.NotFound);
    }

    private static WebApplicationFactory<Program> CreateProbeFactory(string environment) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            // Probe endpoints exist only in this test host, never in the deployed API.
            builder.ConfigureTestServices(services => services.AddControllers()
                .AddApplicationPart(typeof(FoundationProbeController).Assembly));
        });

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.Equal(response.Headers.GetValues("X-Trace-Id").Single(), problem.GetProperty("traceId").GetString());
        return problem;
    }
}

[ApiController, AllowAnonymous]
[Route("_tests")]
public class FoundationProbeController : ControllerBase
{
    [HttpGet("failure")]
    public IActionResult Failure() => throw new InvalidOperationException("secret-marker");

    [HttpPost("validation")]
    public IActionResult Validate(ProbeRequest request) => Ok(request);
}

public class ProbeRequest
{
    [Required, StringLength(10)]
    public string Name { get; set; } = "";
}
