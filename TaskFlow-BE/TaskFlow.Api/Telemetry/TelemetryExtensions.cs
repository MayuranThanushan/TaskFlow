namespace TaskFlow.Api.Telemetry;

public static class TelemetryExtensions
{
    public static IServiceCollection AddServiceTelemetry(this IServiceCollection services)
    {
        // Resolve after the final host configuration is assembled, not while
        // Program is still registering services (also supports test-host overrides).
        services.AddSingleton(sp => TelemetrySettings.Read(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<ServiceTelemetry>();
        services.AddSingleton<TelemetryExportService>();
        services.AddHostedService(sp => sp.GetRequiredService<TelemetryExportService>());
        return services;
    }
}
