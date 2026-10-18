using TaskFlow.Api.Extensions;
using TaskFlow.Api.Middleware;
using TaskFlow.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Keep local and hosted logging independent of Windows Event Log permissions.
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options =>
{
    options.UseUtcTimestamp = true;
    options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
    options.IncludeScopes = true;
});
builder.Logging.AddDebug();

builder.Services.AddApiFoundation();
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddSessionAuthentication(builder.Configuration, builder.Environment);

var app = builder.Build();

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

// Never redirect an auth request that was submitted over an insecure connection.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/auth"))
    {
        context.Response.Headers.CacheControl = "no-store";
        if (!context.Request.IsHttps)
        {
            var problem = ProblemResponses.Create(context, StatusCodes.Status400BadRequest);
            problem.Title = "HTTPS is required for authentication.";
            await ProblemResponses.WriteAsync(context, problem);
            return;
        }
    }
    await next(context);
});

// The local HTTP profile supports foundation checks; deployed traffic requires HTTPS.
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();

app.Run();

public partial class Program;
