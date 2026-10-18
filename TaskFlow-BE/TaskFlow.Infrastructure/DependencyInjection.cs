using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Infrastructure.Identity;
using TaskFlow.Infrastructure.Persistence;
using TaskFlow.Application.Features.Auth;
using TaskFlow.Application.Features.Projects;
using TaskFlow.Infrastructure.Projects;

namespace TaskFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddDbContext<TaskFlowDbContext>(options =>
        {
            var connection = configuration.GetConnectionString("TaskFlow");
            if (string.IsNullOrWhiteSpace(connection))
                throw new InvalidOperationException("Configure ConnectionStrings__TaskFlow before using persistence.");
            options.UseMySQL(connection);
        });
        services.AddIdentityCore<ApplicationUser>(options =>
        {
            options.User.RequireUniqueEmail = true;
            options.Password.RequiredLength = 12;
            options.Password.RequireDigit = true;
            options.Password.RequireUppercase = true;
            options.Password.RequireLowercase = true;
            options.Password.RequireNonAlphanumeric = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        }).AddRoles<IdentityRole<long>>().AddEntityFrameworkStores<TaskFlowDbContext>().AddSignInManager();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<IProjectAccess, ProjectAccess>();
        services.AddScoped<IProjectTransactions, ProjectTransactions>();
        services.AddScoped<IAccountService, AccountService>();
        services.AddSingleton<UnknownUserPasswordCheck>();
        return services;
    }
}
