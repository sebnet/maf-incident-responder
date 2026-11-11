using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace IncidentLab.Chaos;

public static class ChaosExtensions
{
    public static IServiceCollection AddChaos(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ChaosState>();
        return services;
    }

    /// <summary>Register early in the pipeline so faults affect everything after it.</summary>
    public static IApplicationBuilder UseChaos(this IApplicationBuilder app) => app.UseMiddleware<ChaosMiddleware>();

    /// <summary>
    /// Control plane for the eval runner / you. The responder's tools must NEVER call these endpoints:
    /// that would be reading the answer key.
    /// </summary>
    public static IEndpointRouteBuilder MapChaosEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/chaos");

        group.MapGet("/", (ChaosState state) => state.Current is { } fault ? Results.Ok(fault) : Results.NoContent());

        group.MapPost("/", (ChaosRequest request, ChaosState state) =>
        {
            var fault = state.Activate(request);
            return Results.Ok(fault);
        });

        group.MapDelete("/", (ChaosState state) =>
        {
            state.Clear();
            return Results.NoContent();
        });

        return endpoints;
    }
}
