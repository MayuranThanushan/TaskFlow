using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.Outbox;
using TaskFlow.Infrastructure.Persistence;

try
{
    var builder = Host.CreateApplicationBuilder();
    if (builder.Configuration["TASKFLOW_SECRETS_DIRECTORY"] is { Length: > 0 } directory)
        builder.Configuration.AddKeyPerFile(source => { source.FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(directory));
            source.Optional = false; source.ReloadOnChange = false; source.IgnoreCondition = name => name.StartsWith('.') || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase); });
    builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole(); builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddDbContext<TaskFlowDbContext>(options => options.UseMySQL(builder.Configuration.GetConnectionString("TaskFlow") ?? throw new InvalidOperationException()));
    builder.Services.AddScoped<OutboxStore>(); builder.Services.AddSingleton<OutboxMetrics>(); builder.Services.AddSingleton<IOutboxConsumer, CheckpointConsumer>(); builder.Services.AddSingleton<OutboxPump>();
    using var host = builder.Build();
    await using (var check = host.Services.CreateAsyncScope())
    {
        var db = check.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        if (!db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal).SetEquals(await db.Database.GetAppliedMigrationsAsync())) throw new InvalidOperationException();
    }
    var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TaskFlow.Worker");
    if (args.Length == 3 && args[0] == "--replay" && long.TryParse(args[1], out var workspace) && workspace > 0 && long.TryParse(args[2], out var delivery) && delivery > 0)
    {
        await using var scope = host.Services.CreateAsyncScope(); var changed = await scope.ServiceProvider.GetRequiredService<OutboxStore>().ReplayAsync(workspace, delivery);
        logger.LogInformation(new EventId(3302, "outbox.replay"), "Operator replay accepted: {Accepted}", changed); Environment.ExitCode = changed ? 0 : 2; return;
    }
    if (args.Length > 1 || args.Length == 1 && args[0] != "--once") throw new InvalidOperationException();
    await host.StartAsync(); logger.LogInformation(new EventId(3300, "worker.started"), "Checkpoint worker started; readiness follows queue sampling; no external delivery adapter enabled");
    var stop = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
    do
    {
        try { var count = await host.Services.GetRequiredService<OutboxPump>().RunBatchAsync(token: stop); logger.LogInformation(new EventId(3301, "outbox.batch"), "Checkpoint attempts finalized: {Count}", count); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
        catch (Exception failure) { host.Services.GetRequiredService<OutboxMetrics>().Unavailable(); logger.LogError(new EventId(3303, "outbox.failure"), "Worker cycle failed ({ExceptionType}); payload and diagnostics withheld", failure.GetType().Name); if (args.Length == 1) { Environment.ExitCode = 1; break; } }
        if (args.Length == 1) break;
        try { await Task.Delay(TimeSpan.FromSeconds(5), stop); } catch (OperationCanceledException) { break; }
    } while (!stop.IsCancellationRequested);
    await host.StopAsync();
}
catch (Exception) { Console.Error.WriteLine("{\"event\":\"worker.start_failed\",\"message\":\"Verify schema, private configuration and worker grants; diagnostics withheld\"}"); Environment.ExitCode = 1; }
