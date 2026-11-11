using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentLab.Platform;

public static class RuntimeOverrideExtensions
{
    /// <summary>Prefix of the lab's control endpoints. ServiceDefaults keeps it out of traces.</summary>
    public const string OpsPath = "/_ops";

    public static IServiceCollection AddRuntimeOverrides(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<RuntimeOverrideState>();
        return services;
    }

    /// <summary>Register early in the pipeline so overrides affect everything after it.</summary>
    public static IApplicationBuilder UseRuntimeOverrides(this IApplicationBuilder app) => app.UseMiddleware<RequestPipelineMiddleware>();

    /// <summary>
    /// Control plane for the eval runner / you (GET, POST, DELETE <c>/_ops/runtime</c>). The responder's tools must
    /// NEVER call these endpoints: that would be reading the answer key.
    /// </summary>
    public static IEndpointRouteBuilder MapRuntimeOverrideEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup($"{OpsPath}/runtime");

        group.MapGet("/", (RuntimeOverrideState state) => state.Current is { } active ? Results.Ok(active) : Results.NoContent());

        group.MapPost("/", (RuntimeOverrideRequest request, RuntimeOverrideState state) =>
        {
            var active = state.Activate(request);
            return Results.Ok(active);
        });

        group.MapDelete("/", (RuntimeOverrideState state) =>
        {
            state.Clear();
            return Results.NoContent();
        });

        return endpoints;
    }
}
