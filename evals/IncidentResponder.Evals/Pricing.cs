namespace IncidentResponder.Evals;

/// <summary>
/// List price of a model deployment in USD per million tokens, from the "Prices" config section (keyed by
/// deployment name). Fill it from the Azure pricing page for your deployment: never guess a price.
/// </summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal CachedInputPerMillion, decimal OutputPerMillion);

public static class CostEstimator
{
    /// <summary>
    /// Estimated cost in USD, or null when it can't be known: no price configured, or tokens not reported. Never 0
    /// for "unknown", which would look free and pull averages down.
    /// Assumes the OpenAI convention that cached tokens are part of input and reasoning tokens part of output; a
    /// run where cached > input breaks that assumption and is reported as unknown instead of being priced wrong.
    /// </summary>
    public static decimal? Estimate(ModelPrice? price, RunMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (price is null || metrics.InputTokens is not { } input || metrics.OutputTokens is not { } output)
        {
            return null;
        }

        var cached = metrics.CachedInputTokens ?? 0; // unknown cache: price everything as fresh input (an upper bound)
        if (cached > input)
        {
            return null;
        }

        return ((input - cached) * price.InputPerMillion
                + cached * price.CachedInputPerMillion
                + output * price.OutputPerMillion) / 1_000_000m;
    }
}
