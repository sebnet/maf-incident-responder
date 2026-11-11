using Microsoft.AspNetCore.Http;

namespace IncidentLab.Platform;

/// <summary>
/// Fault injection: applies the active <see cref="RuntimeOverride"/> (if any) to incoming requests.
/// Infrastructure paths are never affected. The effect must look like a real failure in telemetry: real exceptions,
/// real latency. This class name appears in every stack trace of the service, so it stays neutral (AGENTS.md §6.5).
/// </summary>
public sealed class RequestPipelineMiddleware(RequestDelegate next, RuntimeOverrideState state)
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(2);

    public async Task InvokeAsync(HttpContext context)
    {
        var active = state.Current;
        if (active is null || IsInfrastructurePath(context.Request.Path) || Random.Shared.NextDouble() >= active.Probability)
        {
            await next(context);
            return;
        }

        switch (active.Kind)
        {
            case OverrideKind.Latency:
                await Task.Delay(active.Delay ?? DefaultDelay, context.RequestAborted);
                await next(context);
                break;

            case OverrideKind.Errors:
                // A real exception: ASP.NET Core logs it as unhandled and returns 500, with a proper stack trace
                // in the trace/span. That is what a responder sees in production.
                throw new InvalidOperationException(active.ErrorMessage ?? "Object reference not set to an instance of an object.");

            default:
                await next(context);
                break;
        }
    }

    private static bool IsInfrastructurePath(PathString path) =>
        path.StartsWithSegments(RuntimeOverrideExtensions.OpsPath) || path.StartsWithSegments("/health") || path.StartsWithSegments("/alive");
}
