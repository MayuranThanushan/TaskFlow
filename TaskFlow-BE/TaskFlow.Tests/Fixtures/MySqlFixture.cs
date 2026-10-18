using Microsoft.EntityFrameworkCore;
using MySql.Data.MySqlClient;
using TaskFlow.Infrastructure.Persistence;

namespace TaskFlow.Tests.Fixtures;

public sealed class MySqlFactAttribute : FactAttribute
{
    public MySqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TASKFLOW_TEST_CONNECTION"))
            && !MySqlFixture.IsRequired)
            Skip = "Set TASKFLOW_TEST_CONNECTION to run live MySQL tests.";
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MySqlCollection : ICollectionFixture<MySqlFixture>
{
    public const string Name = "MySQL integration";
}

public sealed class MySqlFixture : IAsyncLifetime
{
    private string? connection;
    public static bool IsRequired => string.Equals(
        Environment.GetEnvironmentVariable("TASKFLOW_REQUIRE_MYSQL"), "true", StringComparison.OrdinalIgnoreCase);

    public string ConnectionString => connection
        ?? throw new InvalidOperationException("A dedicated MySQL test connection is required.");

    public async Task InitializeAsync()
    {
        var configured = Environment.GetEnvironmentVariable("TASKFLOW_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (IsRequired) throw new InvalidOperationException("CI requires TASKFLOW_TEST_CONNECTION; live tests cannot be skipped.");
            return;
        }

        ValidateConnection(configured);
        connection = configured;
        await using var context = CreateContext();
        // Migrate once per collection. No EnsureDeleted, DROP DATABASE, or shared DbContext.
        await context.Database.MigrateAsync();
        await context.Database.OpenConnectionAsync();
        if (!context.Database.GetDbConnection().ServerVersion.StartsWith("8.4.", StringComparison.Ordinal))
            throw new InvalidOperationException("The integration fixture requires the MySQL 8.4 server line.");
    }

    public TaskFlowDbContext CreateContext(TimeProvider? clock = null) => new(
        new DbContextOptionsBuilder<TaskFlowDbContext>().UseMySQL(ConnectionString).Options,
        clock ?? TimeProvider.System);

    internal static void ValidateConnection(string connection)
    {
        MySqlConnectionStringBuilder parsed;
        try { parsed = new MySqlConnectionStringBuilder(connection); }
        catch (ArgumentException) { throw new InvalidOperationException("Invalid MySQL test connection configuration."); }
        if (parsed.Database != "taskflow_test" && !parsed.Database.StartsWith("taskflow_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Integration tests require a taskflow_test database; development/production databases are forbidden.");
        if (string.IsNullOrWhiteSpace(parsed.UserID) || string.Equals(parsed.UserID, "root", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Use a dedicated non-root MySQL test account.");
    }

    // Every persistence test owns a fresh context and rolls back its own transaction.
    // The runner/container owns database provisioning and shutdown, never the test fixture.
    public Task DisposeAsync() => Task.CompletedTask;
}
