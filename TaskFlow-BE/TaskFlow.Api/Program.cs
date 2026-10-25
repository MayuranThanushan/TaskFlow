using TaskFlow.Api.Extensions;
using TaskFlow.Api.Middleware;
using TaskFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["TASKFLOW_SECRETS_DIRECTORY"] is { Length: > 0 } secretsDirectory)
    builder.Configuration.AddKeyPerFile(source =>
    {
        source.FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(Path.GetFullPath(secretsDirectory));
        source.Optional = false;
        source.ReloadOnChange = false;
        source.IgnoreCondition = name => name.StartsWith('.') || name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase);
    });

// Keep local and hosted logging independent of Windows Event Log permissions.
builder.Logging.ClearProviders();
if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Local"))
    builder.Logging.AddSimpleConsole(options =>
    {
        options.SingleLine = true;
        options.TimestampFormat = "HH:mm:ss ";
        options.UseUtcTimestamp = true;
        options.IncludeScopes = false;
    });
else
    builder.Logging.AddJsonConsole(options =>
    {
        options.UseUtcTimestamp = true;
        options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        options.IncludeScopes = false;
    });

builder.Services.AddApiFoundation();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSessionAuthentication(builder.Configuration, builder.Environment);

var app = builder.Build();
if (app.Configuration.GetValue("FileLogging:Enabled", app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Local")))
{
    var logDirectory = app.Configuration["FileLogging:Directory"]
        ?? Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, "../../.local/logs"));
    app.Services.GetRequiredService<ILoggerFactory>().AddProvider(new TaskFlow.Api.Logging.SiemFileLoggerProvider(
        logDirectory, app.Environment.EnvironmentName,
        app.Configuration.GetValue<long>("FileLogging:MaxBytes", 10 * 1024 * 1024),
        app.Configuration.GetValue("FileLogging:RetainedFiles", 14)));
}
if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("TaskFlow")))
    app.Logger.LogWarning(new EventId(2001, "database.configuration_missing"),
        "Database connection is not configured. Set ConnectionStrings__TaskFlow in the API terminal; readiness and login will fail.");

var requireHttps = app.Configuration.GetValue("Transport:RequireHttps",
    !app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Local"));

app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(async context =>
{
    await ProblemResponses.WriteAsync(context.HttpContext,
        ProblemResponses.Create(context.HttpContext, context.HttpContext.Response.StatusCode));
});

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Strict deployments reject insecure credentials; optional transport also supports local HTTP.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/auth"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (requireHttps && !context.Request.IsHttps)
        {
            var problem = ProblemResponses.Create(context, StatusCodes.Status400BadRequest);
            problem.Title = "HTTPS is required for authentication.";
            await ProblemResponses.WriteAsync(context, problem);
            return;
        }
    }
    await next(context);
});

// HSTS and redirects must agree with the configured transport policy.
if (requireHttps && !app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Local")
    && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    await next(context);
});

// Public compiled assets contain no credentials; all data remains behind the API's authorization.
if (Directory.Exists(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/api")
    && !context.Request.Path.StartsWithSegments("/openapi"), branch => branch.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
        context.Context.Request.Path.StartsWithSegments("/assets")
            ? "public,max-age=31536000,immutable" : "no-cache"
}));

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapFallback(async context =>
{
    // API/OpenAPI misses must remain JSON 404s, never the SPA entry document.
    var path = context.Request.Path;
    var index = Path.Combine(app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot"), "index.html");
    if (path.StartsWithSegments("/api") || path.StartsWithSegments("/openapi") || !File.Exists(index))
    {
        await ProblemResponses.WriteAsync(context, ProblemResponses.Create(context, StatusCodes.Status404NotFound));
        return;
    }
    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    await context.Response.SendFileAsync(index, context.RequestAborted);
}).WithMetadata(new HttpMethodMetadata(["GET", "HEAD"])).AllowAnonymous();

app.Run();

public partial class Program;
