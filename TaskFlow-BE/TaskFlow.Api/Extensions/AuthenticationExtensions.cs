using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using TaskFlow.Api.Middleware;
using TaskFlow.Infrastructure.Identity;

namespace TaskFlow.Api.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddSessionAuthentication(this IServiceCollection services,
        IConfiguration configuration, IHostEnvironment environment)
    {
        var keysPath = configuration["DataProtection:KeysPath"]
            ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "../../.local/data-protection"));
        services.AddDataProtection().SetApplicationName("TaskFlow").PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = IdentityConstants.ApplicationScheme;
            options.DefaultChallengeScheme = IdentityConstants.ApplicationScheme;
            options.DefaultSignInScheme = IdentityConstants.ApplicationScheme;
        }).AddIdentityCookies();
        services.AddAuthorization();
        services.Configure<SecurityStampValidatorOptions>(options => options.ValidationInterval = TimeSpan.Zero);
        services.AddOptions<SecurityStampValidatorOptions>().Configure<TimeProvider>((options, clock) => options.TimeProvider = clock);
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme).Configure<TimeProvider>((options, clock) =>
        {
            var requireHttps = configuration.GetValue("Transport:RequireHttps",
                !environment.IsDevelopment() && !environment.IsEnvironment("Local"));
            options.TimeProvider = clock;
            options.Cookie.Name = requireHttps ? "__Host-TaskFlow.Auth" : "TaskFlow.Auth";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = async context =>
            {
                var signIn = context.HttpContext.RequestServices.GetRequiredService<SignInManager<ApplicationUser>>();
                if (await signIn.ValidateSecurityStampAsync(context.Principal) is null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                }
                // Explicit stamp validation runs on every request and never renews the ticket.
            };
        });
        services.AddAntiforgery(options =>
        {
            var requireHttps = configuration.GetValue("Transport:RequireHttps",
                !environment.IsDevelopment() && !environment.IsEnvironment("Local"));
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = requireHttps ? "__Host-TaskFlow.Csrf" : "TaskFlow.Csrf";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.Path = "/";
        });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("auth", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true
                }));
            options.AddPolicy("governance", context => RateLimitPartition.GetFixedWindowLimiter(
                context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value is { } actor ? "actor:" + actor : "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.OnRejected = async (context, token) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
                    context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                await ProblemResponses.WriteAsync(context.HttpContext,
                    ProblemResponses.Create(context.HttpContext, StatusCodes.Status429TooManyRequests));
            };
        });
        return services;
    }
}
