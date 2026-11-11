using Microsoft.AspNetCore.Http;

namespace IncidentLab.Chaos;

/// <summary>
/// Applies the active fault (if any) to incoming requests. Infrastructure paths are never affected.
/// Faults must look like real failures in telemetry: real exceptions, real latency, no "chaos" wording.
/// </summary>
public sealed class ChaosMiddleware(RequestDelegate next, ChaosState state)
{
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromSeconds(2);

    public async Task InvokeAsync(HttpContext context)
    {
        var fault = state.Current;
        if (fault is null || IsInfrastructurePath(context.Request.Path) || Random.Shared.NextDouble() >= fault.Probability)
        {
            await next(context);
            return;
        }

        switch (fault.Kind)
        {
            case FaultKind.Latency:
                await Task.Delay(fault.Delay ?? DefaultDelay, context.RequestAborted);
                await next(context);
                break;

            case FaultKind.Errors:
                // A real exception: ASP.NET Core logs it as unhandled and returns 500, with a proper stack trace
                // in the trace/span. That is what a responder sees in production.
                throw new InvalidOperationException(fault.ErrorMessage ?? "Object reference not set to an instance of an object.");

            default:
                await next(context);
                break;
        }
    }

    private static bool IsInfrastructurePath(PathString path) =>
        path.StartsWithSegments("/chaos") || path.StartsWithSegments("/health") || path.StartsWithSegments("/alive");
}
