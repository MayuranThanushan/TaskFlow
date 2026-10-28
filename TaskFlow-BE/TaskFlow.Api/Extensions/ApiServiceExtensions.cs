using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using TaskFlow.Api.Middleware;
using TaskFlow.Api.Filters;

namespace TaskFlow.Api.Extensions;

public static class ApiServiceExtensions
{
    public static IServiceCollection AddApiFoundation(this IServiceCollection services)
    {
        services.AddScoped<WorkspaceReadFilter>();
        services.AddScoped<WorkspaceLifecycleFilter>();
        services.AddScoped<LegacyResourceFilter>();
        services.AddScoped<WorkspaceWriteFilter>();
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            ProblemResponses.Enrich(context.HttpContext, context.ProblemDetails));
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.AddControllers(options =>
            {
                // The filter-factory order must be explicit so protected writes authenticate before CSRF validation.
                options.Filters.Add<AntiforgeryProblemFilter>(1000);
                options.Filters.Add<LegacyResourceFilter>(400);
                options.Filters.Add(new AuthorizeFilter());
                options.ModelMetadataDetailsProviders.Add(new SystemTextJsonValidationMetadataProvider());
            })
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
                options.JsonSerializerOptions.AllowDuplicateProperties = false;
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
            })
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(entry => entry.Value?.Errors.Count > 0)
                    .ToDictionary(entry => entry.Key, entry => entry.Value!.Errors.Select(error =>
                        error.Exception is not null || entry.Key.StartsWith('$')
                            ? "The request body is invalid."
                            : error.ErrorMessage).ToArray());
                var problem = new ValidationProblemDetails(errors)
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred."
                };
                ProblemResponses.Enrich(context.HttpContext, problem);
                var result = new BadRequestObjectResult(problem);
                result.ContentTypes.Add("application/problem+json");
                return result;
            });
        services.AddHealthChecks().AddCheck<TaskFlow.Api.Health.DatabaseReadinessCheck>(
            "database", tags: ["ready"], timeout: TimeSpan.FromSeconds(5));
        services.AddOpenApi(options =>
        {
            options.AddSchemaTransformer<TaskFlow.Api.Contracts.ContractSchemaTransformer>();
            options.AddOperationTransformer<TaskFlow.Api.Contracts.ContractOperationTransformer>();
            options.AddDocumentTransformer<TaskFlow.Api.Contracts.ContractDocumentTransformer>();
        });
        return services;
    }
}
