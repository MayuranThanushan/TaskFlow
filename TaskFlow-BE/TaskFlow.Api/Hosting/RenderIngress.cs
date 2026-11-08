using Microsoft.AspNetCore.DataProtection.KeyManagement;

namespace TaskFlow.Api.Hosting;

public static class RenderIngress
{
    public static void Configure(WebApplication app)
    {
        if (app.Configuration["DataProtection:ReadOnlyKeyRingBase64"] is { Length: > 0 })
        {
            var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
            IKey[] keys;
            try { keys = app.Services.GetRequiredService<IKeyManager>().GetAllKeys().Where(k => !k.IsRevoked && k.ActivationDate <= now && k.ExpirationDate > now).ToArray(); }
            catch { throw new InvalidOperationException("Hosted key material could not be read. Restore the protected secret; private diagnostics are suppressed."); }
            if (keys.Length == 0) throw new InvalidOperationException("Hosted key ring has no active key. Rotate the protected secret before starting the service.");
            if (keys.Max(k => k.ExpirationDate) < now.AddDays(14)) app.Logger.LogWarning(new EventId(2006, "hosting.key_rotation_due"), "Hosted key rotation is due within 14 days. Retain existing keys when updating the secret.");
        }
        if (!app.Configuration.GetValue<bool>("Hosting:RenderEdge")) return;
        var host = app.Configuration["RENDER_EXTERNAL_HOSTNAME"];
        // Explicit opt-in for a Render Free service, whose only public path is its HTTPS edge.
        // Do not use this mode for an exposed container port, TCP proxy, or private ingress.
        if (app.Configuration["RENDER"] != "true" || app.Configuration["RENDER_SERVICE_TYPE"] != "web"
            || app.Configuration["DataProtection:ReadOnlyKeyRingBase64"] is not { Length: > 0 }
            || string.IsNullOrWhiteSpace(app.Configuration["RENDER_SERVICE_ID"])
            || host is null || Uri.CheckHostName(host) != UriHostNameType.Dns || !host.EndsWith(".onrender.com", StringComparison.OrdinalIgnoreCase)
            || !app.Configuration.GetValue("Transport:RequireHttps", true))
            throw new InvalidOperationException("Render edge mode requires a Render web service with its managed hostname and strict HTTPS transport.");
        app.Use(async (context, next) =>
        {
            // Host is not taken from X-Forwarded-Host. Raw client-IP headers remain untrusted:
            // auth rate limiting conservatively uses the socket peer until a trusted IP path is verified.
            if (string.Equals(context.Request.Host.Host, host, StringComparison.OrdinalIgnoreCase)
                && context.Request.Headers["X-Forwarded-Proto"].Count == 1
                && context.Request.Headers["X-Forwarded-Proto"][0] == "https") context.Request.Scheme = "https";
            await next(context);
        });
    }
}
