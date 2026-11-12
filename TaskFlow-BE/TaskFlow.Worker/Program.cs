using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
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
    // Explicit offline preview command; never consumes checkpoint backlog or reads the database.
    if (args.Length == 2 && args[0] == "--capture-email")
    {
        var emailOptions = builder.Configuration.GetSection("Email").Get<TaskFlow.Infrastructure.Email.LocalEmailOptions>() ?? new();
        var sender = new TaskFlow.Infrastructure.Email.LocalEmailSender(emailOptions, builder.Environment.EnvironmentName, TimeProvider.System);
        Environment.ExitCode = await TaskFlow.Infrastructure.Email.EmailCaptureCommand.RunAsync(args[1], sender, Console.Out);
        return;
    }
    using var emailHttp = TaskFlow.Infrastructure.Email.SendGridEmailSender.CreateClient();
    // Explicit provider test only; the capture command above can never use the network.
    if (args.Length == 2 && args[0] == "--send-email")
    {
        var options = builder.Configuration.GetSection("Email").Get<TaskFlow.Infrastructure.Email.LocalEmailOptions>() ?? new();
        var sender = new TaskFlow.Infrastructure.Email.SendGridEmailSender(options, builder.Environment.EnvironmentName, emailHttp, TimeProvider.System);
        Environment.ExitCode = await TaskFlow.Infrastructure.Email.EmailCaptureCommand.RunAsync(args[1], sender, Console.Out);
        return;
    }
    builder.Logging.ClearProviders(); builder.Logging.AddJsonConsole(); builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.None);
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddDbContext<TaskFlowDbContext>(options => options.UseMySQL(TaskFlowConnectionString.Normalize(builder.Configuration.GetConnectionString("TaskFlow") ?? throw new PersistenceConfigurationException())));
    builder.Services.AddScoped<OutboxStore>(); builder.Services.AddSingleton<OutboxMetrics>(); builder.Services.AddSingleton<IOutboxConsumer, CheckpointConsumer>(); builder.Services.AddSingleton<OutboxPump>();
    var invitationMail = args.Length == 1 && args[0] == "--invitations-once";
    var accountMail = args.Length == 1 && args[0] == "--account-mail-once";
    if (accountMail || invitationMail)
    {
        if (!builder.Environment.IsDevelopment() && !builder.Environment.IsStaging()) throw new InvalidOperationException();
        var keyPath = builder.Configuration["DataProtection:KeysPath"] ?? throw new InvalidOperationException();
        builder.Services.AddDataProtection().SetApplicationName("TaskFlow").PersistKeysToFileSystem(new DirectoryInfo(keyPath));
        var options = builder.Configuration.GetSection("Email").Get<TaskFlow.Infrastructure.Email.LocalEmailOptions>() ?? new();
        builder.Services.AddSingleton<TaskFlow.Application.Features.Email.IEmailSender>(options.Mode == "SendGrid"
            ? new TaskFlow.Infrastructure.Email.SendGridEmailSender(options, builder.Environment.EnvironmentName, emailHttp, TimeProvider.System)
            : new TaskFlow.Infrastructure.Email.LocalEmailSender(options, builder.Environment.EnvironmentName, TimeProvider.System));
        builder.Services.AddScoped<TaskFlow.Infrastructure.Email.AccountMailCapture>();
        builder.Services.AddScoped<TaskFlow.Infrastructure.Email.InvitationMailCapture>();
    }
    using var host = builder.Build();
    await using (var check = host.Services.CreateAsyncScope())
    {
        var db = check.ServiceProvider.GetRequiredService<TaskFlowDbContext>();
        if (!db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal).SetEquals(await db.Database.GetAppliedMigrationsAsync())) throw new InvalidOperationException();
    }
    if (accountMail || invitationMail)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var count = invitationMail ? await scope.ServiceProvider.GetRequiredService<TaskFlow.Infrastructure.Email.InvitationMailCapture>().RunAsync() : await scope.ServiceProvider.GetRequiredService<TaskFlow.Infrastructure.Email.AccountMailCapture>().RunAsync();
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { eventId = 3401, code = invitationMail ? "invitation_mail.batch_completed" : "account_mail.batch_completed", count })); return;
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
