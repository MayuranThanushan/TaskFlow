using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Domain.Entities;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Tests.Fixtures;

namespace TaskFlow.Tests;

[Collection(MySqlCollection.Name)]
public sealed class ProjectApiTests(MySqlFixture fixture)
{
    [MySqlFact, Trait("Category", "MySql")]
    public async Task CreationRequiresAuthenticationAndCsrfAndCommitsOwnerAndActivity()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync();
        using var anonymous = host.Client();
        await Problem(await anonymous.GetAsync("/api/v1/projects"), HttpStatusCode.Unauthorized);
        await Problem(await anonymous.PostAsJsonAsync("/api/v1/projects", new { name = "Private" }), HttpStatusCode.Unauthorized);
        using var owner = await host.LoginAsync(0);
        await Problem(await owner.PostAsJsonAsync("/api/v1/projects", new { name = "Private" }), HttpStatusCode.BadRequest);
        var response = await host.CreateAsync(owner, " Launch ", " Description ");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var project = await Body(response);
        Assert.Equal("Launch", project.GetProperty("name").GetString());
        Assert.Equal("Description", project.GetProperty("description").GetString());
        Assert.Equal(host.Users[0].Id.ToString(), project.GetProperty("ownerId").GetString());
        Assert.Equal(JsonValueKind.String, project.GetProperty("id").ValueKind);
        Assert.EndsWith("Z", project.GetProperty("createdAtUtc").GetString());
        Assert.Equal(6, project.EnumerateObject().Count());
        Assert.EndsWith("/api/v1/projects/" + project.GetProperty("id").GetString(), response.Headers.Location!.ToString());
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync(response.Headers.Location)).StatusCode);
        await using var database = fixture.CreateContext();
        var id = long.Parse(project.GetProperty("id").GetString()!);
        var activity = await database.ActivityLogs.SingleAsync(activity => activity.ProjectId == id);
        Assert.Equal(ActivityAction.ProjectCreated, activity.ActionType);
        Assert.Equal(host.Users[0].Id, activity.ActorUserId);
        Assert.False(await database.ProjectMembers.AnyAsync(member => member.ProjectId == id));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task MembersSeeOnlySharedProjectsOutsidersCannotReadAndDeletionAndRemovalTakeEffect()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync();
        using var owner = await host.LoginAsync(0); using var member = await host.LoginAsync(1); using var outsider = await host.LoginAsync(2);
        var shared = await Body(await host.CreateAsync(owner, "Shared")); var privateProject = await Body(await host.CreateAsync(owner, "Private"));
        var hidden = await Body(await host.CreateAsync(owner, "Deleted"));
        var sharedId = long.Parse(shared.GetProperty("id").GetString()!); var hiddenId = long.Parse(hidden.GetProperty("id").GetString()!);
        await using var database = fixture.CreateContext();
        database.ProjectMembers.Add(new ProjectMember { ProjectId = sharedId, UserId = host.Users[1].Id });
        database.ProjectMembers.Add(new ProjectMember { ProjectId = hiddenId, UserId = host.Users[1].Id });
        var deleted = await database.Projects.SingleAsync(project => project.Id == hiddenId); deleted.DeletedAtUtc = DateTime.UtcNow;
        await database.SaveChangesAsync();
        var memberList = await Body(await member.GetAsync("/api/v1/projects"));
        Assert.Equal(1, memberList.GetProperty("totalCount").GetInt32());
        Assert.Equal("Shared", memberList.GetProperty("items")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync("/api/v1/projects/" + sharedId)).StatusCode);
        await Problem(await member.GetAsync("/api/v1/projects/" + privateProject.GetProperty("id").GetString()), HttpStatusCode.NotFound);
        await Problem(await outsider.GetAsync("/api/v1/projects/" + sharedId), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Body(await outsider.GetAsync("/api/v1/projects"))).GetProperty("totalCount").GetInt32());
        foreach (var client in new[] { owner, member, outsider }) await Problem(await client.GetAsync("/api/v1/projects/" + hiddenId), HttpStatusCode.NotFound);
        database.ProjectMembers.Remove(await database.ProjectMembers.SingleAsync(value => value.ProjectId == sharedId && value.UserId == host.Users[1].Id));
        await database.SaveChangesAsync();
        await Problem(await member.GetAsync("/api/v1/projects/" + sharedId), HttpStatusCode.NotFound);
        Assert.Equal(0, (await Body(await member.GetAsync("/api/v1/projects"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(2, (await Body(await owner.GetAsync("/api/v1/projects"))).GetProperty("totalCount").GetInt32());
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task PaginationIsStableScopedAndSearchIsALiteralCaseInsensitiveSubstring()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync();
        using var owner = await host.LoginAsync(0); using var outsider = await host.LoginAsync(2);
        var ids = new List<string>();
        foreach (var name in new[] { "Same", "Same", "100%_Ready\\", "Different" })
            ids.Add((await Body(await host.CreateAsync(owner, name))).GetProperty("id").GetString()!);
        await host.CreateAsync(outsider, "100%_Ready\\");
        var first = await Body(await owner.GetAsync("/api/v1/projects?pageSize=2"));
        var second = await Body(await owner.GetAsync("/api/v1/projects?pageSize=2&page=2"));
        Assert.Equal(4, first.GetProperty("totalCount").GetInt32()); Assert.Equal(2, first.GetProperty("totalPages").GetInt32());
        Assert.Equal(new[] { ids[3], ids[2], ids[1], ids[0] }, first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray()).Select(value => value.GetProperty("id").GetString()).ToArray());
        foreach (var search in new[] { "%_", "READY", "\\" })
        {
            var page = await Body(await owner.GetAsync("/api/v1/projects?search=" + Uri.EscapeDataString(search)));
            Assert.Equal(1, page.GetProperty("totalCount").GetInt32());
            Assert.Equal(ids[2], page.GetProperty("items")[0].GetProperty("id").GetString());
        }
        var names = await Body(await owner.GetAsync("/api/v1/projects?search=Same&sortBy=name&sortDirection=asc"));
        Assert.Equal(new[] { ids[1], ids[0] }, names.GetProperty("items").EnumerateArray().Select(value => value.GetProperty("id").GetString()).ToArray());
        var empty = await Body(await owner.GetAsync("/api/v1/projects?page=100&pageSize=2"));
        Assert.Equal(4, empty.GetProperty("totalCount").GetInt32()); Assert.Empty(empty.GetProperty("items").EnumerateArray());
        var noMatches = await Body(await owner.GetAsync("/api/v1/projects?search=absent"));
        Assert.Equal(0, noMatches.GetProperty("totalPages").GetInt32());
        var ascending = await Body(await owner.GetAsync("/api/v1/projects?sortBy=updatedAtUtc&sortDirection=asc"));
        Assert.Equal(ids[3], ascending.GetProperty("items")[0].GetProperty("id").GetString()); // Fixed clock: numeric ID breaks ties.
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ValidationRejectsUnknownFieldsBadQueriesAndInvalidIdsWithoutCreatingProjects()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        foreach (var body in new[] { "{}", "{\"name\":null}", "{\"name\":\" \"}", "{\"name\":\"A\",\"ownerId\":\"1\"}", "{\"name\":\"A\",\"id\":\"1\"}", "{\"name\":\"A\",\"name\":\"B\"}" })
            await Problem(await host.PostRawAsync(owner, body), HttpStatusCode.BadRequest);
        await Problem(await host.CreateAsync(owner, new string('x', 151)), HttpStatusCode.BadRequest);
        await Problem(await host.CreateAsync(owner, "Valid", new string('x', 10_001)), HttpStatusCode.BadRequest);
        Assert.Equal(0, (await Body(await owner.GetAsync("/api/v1/projects"))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(owner, new string('x', 150), new string('x', 10_000))).StatusCode);
        foreach (var query in new[] { "page=0", "page=-1", "page=abc", "pageSize=101", "pageSize=0", "sortBy=ownerId", "sortDirection=up", "ownerId=1", "page=1&page=2", "page=2147483647&pageSize=100", "search=" + new string('x', 201) })
            await Problem(await owner.GetAsync("/api/v1/projects?" + query), HttpStatusCode.BadRequest);
        foreach (var id in new[] { "0", "-1", "+1", "1e3", "9223372036854775808", "wrong" })
            await Problem(await owner.GetAsync("/api/v1/projects/" + Uri.EscapeDataString(id)), HttpStatusCode.BadRequest);
        await Problem(await owner.GetAsync("/api/v1/projects/9223372036854775807"), HttpStatusCode.NotFound);
        await using var database = fixture.CreateContext();
        Assert.Equal(1, await database.ActivityLogs.CountAsync(activity => activity.ActorUserId == host.Users[0].Id));
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task DetailCountsActiveTasksRoundsMidpointsAndPreservesBigintIdsAsStrings()
    {
        await using var host = new ProjectHost(fixture); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        await using var database = fixture.CreateContext();
        const long id = 9_007_199_254_741_111;
        database.Projects.Add(new Project { Id = id, OwnerId = host.Users[0].Id, Name = "Large ID" }); await database.SaveChangesAsync();
        Assert.Equal(0, (await Body(await owner.GetAsync("/api/v1/projects/" + id))).GetProperty("progressPercentage").GetInt32());
        for (var index = 0; index < 9; index++) database.Tasks.Add(new TaskItem { ProjectId = id, Title = "Task " + index, Status = index < 2 ? TaskState.Done : TaskState.ToDo, DeletedAtUtc = index == 0 ? DateTime.UtcNow : null });
        await database.SaveChangesAsync();
        var detail = await Body(await owner.GetAsync("/api/v1/projects/" + id));
        Assert.Equal(id.ToString(), detail.GetProperty("id").GetString());
        Assert.Equal(8, detail.GetProperty("totalTasks").GetInt32()); Assert.Equal(1, detail.GetProperty("completedTasks").GetInt32()); Assert.Equal(13, detail.GetProperty("progressPercentage").GetInt32());
        Assert.Equal(9, detail.EnumerateObject().Count());
    }

    [MySqlFact, Trait("Category", "MySql")]
    public async Task ActivityInsertFailureReturnsSafe500AndLeavesNoProjectBehind()
    {
        var failure = new FailActivity(); await using var host = new ProjectHost(fixture, failure); await host.InitializeAsync(); using var owner = await host.LoginAsync(0);
        failure.Enabled = true;
        await Problem(await host.CreateAsync(owner, "Must not persist"), HttpStatusCode.InternalServerError);
        await using var database = fixture.CreateContext();
        Assert.False(await database.Projects.AnyAsync(project => project.OwnerId == host.Users[0].Id));
        Assert.False(await database.ActivityLogs.AnyAsync(activity => activity.ActorUserId == host.Users[0].Id));
        failure.Enabled = false;
        Assert.Equal(HttpStatusCode.Created, (await host.CreateAsync(owner, "Recovered")).StatusCode);
    }

    private static async Task<JsonElement> Body(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"Unexpected HTTP {(int)response.StatusCode}");
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
    private static async Task Problem(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.Equal(response.Headers.GetValues("X-Trace-Id").Single(), body.GetProperty("traceId").GetString());
        Assert.DoesNotContain("stackTrace", body.ToString()); Assert.DoesNotContain("foreign key", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }
    private sealed class FixedClock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 10, 18, 8, 0, 0, TimeSpan.Zero); }
    private sealed class FailActivity : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled) foreach (var entry in data.Context!.ChangeTracker.Entries<ActivityLog>().Where(entry => entry.State == EntityState.Added)) entry.Entity.ActorUserId = long.MaxValue;
            return ValueTask.FromResult(result);
        }
    }
    private sealed class ProjectHost : IAsyncDisposable
    {
        private const string Password = "Project-Tests-Password123!";
        private readonly MySqlFixture fixture;
        private readonly WebApplicationFactory<Program> factory;
        public List<ApplicationUser> Users { get; } = [];
        public ProjectHost(MySqlFixture fixture, IInterceptor? failure = null)
        {
            this.fixture = fixture;
            factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:TaskFlow"] = fixture.ConnectionString }));
                builder.ConfigureTestServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FixedClock()));
                    services.AddDataProtection().UseEphemeralDataProtectionProvider();
                    if (failure is not null) services.AddDbContext<TaskFlowDbContext>(options => options.AddInterceptors(failure));
                });
            });
        }
        public async Task InitializeAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope(); var manager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            for (var index = 0; index < 3; index++)
            {
                var email = $"project-{Guid.NewGuid():N}@example.test"; var user = new ApplicationUser { DisplayName = "User " + index, UserName = email, Email = email };
                Users.Add(user); var result = await manager.CreateAsync(user, Password); Assert.True(result.Succeeded);
            }
        }
        public HttpClient Client() => factory.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        public async Task<HttpClient> LoginAsync(int index)
        {
            var client = Client(); var token = (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf")).GetProperty("requestToken").GetString();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { email = Users[index].Email, password = Password }) };
            request.Headers.Add("X-CSRF-TOKEN", token); Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode); return client;
        }
        public Task<HttpResponseMessage> CreateAsync(HttpClient client, string name, string? description = null) => PostRawAsync(client, JsonSerializer.Serialize(new { name, description }));
        public async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string body)
        {
            var token = (await client.GetFromJsonAsync<JsonElement>("/api/v1/auth/csrf")).GetProperty("requestToken").GetString();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/projects") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-CSRF-TOKEN", token); return await client.SendAsync(request);
        }
        public async ValueTask DisposeAsync()
        {
            await factory.DisposeAsync(); await using var database = fixture.CreateContext(); var ids = Users.Select(user => user.Id).ToArray();
            var projects = await database.Projects.IgnoreQueryFilters().Where(project => ids.Contains(project.OwnerId)).Select(project => project.Id).ToArrayAsync();
            await using var transaction = await database.Database.BeginTransactionAsync();
            await database.ActivityLogs.IgnoreQueryFilters().Where(activity => projects.Contains(activity.ProjectId)).ExecuteDeleteAsync();
            await database.Tasks.IgnoreQueryFilters().Where(task => projects.Contains(task.ProjectId)).ExecuteDeleteAsync();
            await database.ProjectMembers.IgnoreQueryFilters().Where(member => projects.Contains(member.ProjectId)).ExecuteDeleteAsync();
            await database.Projects.IgnoreQueryFilters().Where(project => projects.Contains(project.Id)).ExecuteDeleteAsync();
            await database.Users.Where(user => ids.Contains(user.Id)).ExecuteDeleteAsync(); await transaction.CommitAsync();
        }
    }
}
