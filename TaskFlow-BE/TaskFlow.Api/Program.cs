using TaskFlow.Api.Extensions;
using TaskFlow.Api.Middleware;

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

// The local HTTP profile supports foundation checks; deployed traffic requires HTTPS.
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
{
    app.UseHsts();
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapControllers();

app.Run();

public partial class Program;
