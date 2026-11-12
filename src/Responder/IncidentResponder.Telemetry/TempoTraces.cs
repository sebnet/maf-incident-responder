using System.Globalization;
using System.Text.Json;
using IncidentResponder.Core;

namespace IncidentResponder.Telemetry;

/// <summary>
/// Tempo side of <see cref="LgtmTelemetryQuery"/>: turns <c>GET /api/traces/{id}</c> (OTLP JSON) into a
/// <see cref="TraceSummary"/> with the slowest spans and the error spans. See docs/notes/lgtm-labels.md.
/// </summary>
internal static class TempoTraces
{
    public const int SlowestSpans = 5;

    /// <summary>"All error spans", bounded: a trace with many retries can have dozens.</summary>
    public const int MaxErrorSpans = 20;

    private const int MaxErrorLength = 200;

    /// <summary>OTel trace ids: 32 hex chars (16 for legacy 64-bit ids).</summary>
    public static bool IsValidTraceId(string traceId) =>
        traceId.Length is 32 or 16 && traceId.All(char.IsAsciiHexDigit);

    public static TraceSummary Summarize(string traceId, JsonElement root)
    {
        var spans = ReadSpans(root);
        if (spans.Count == 0)
        {
            return new TraceSummary(traceId, TimeSpan.Zero, [], []);
        }

        // Client spans are just "GET" against localhost:<dynamic port>: name them after the service that served them.
        var childServices = spans
            .Where(s => s.ParentId is not null)
            .GroupBy(s => s.ParentId!)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Service).ToList(), StringComparer.Ordinal);

        SpanSummary ToSummary(RawSpan s) => new(s.Service, DisplayName(s, childServices), s.Duration, s.Error);

        var duration = TimeSpan.FromTicks((spans.Max(s => s.EndNanos) - spans.Min(s => s.StartNanos)) / 100);
        var slowest = spans.OrderByDescending(s => s.Duration).Take(SlowestSpans).Select(ToSummary).ToList();
        var errors = spans.Where(s => s.Error is not null).OrderBy(s => s.StartNanos).Take(MaxErrorSpans).Select(ToSummary).ToList();

        return new TraceSummary(traceId, duration, slowest, errors);
    }

    private static string DisplayName(RawSpan s, Dictionary<string, List<string>> childServices)
    {
        if (s.Kind != "SPAN_KIND_CLIENT")
        {
            return s.Name;
        }

        var name = s.Name;
        if (s.Attributes.TryGetValue("url.full", out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            name += " " + uri.AbsolutePath;
        }

        var callee = childServices.GetValueOrDefault(s.Id)?.FirstOrDefault(c => c != s.Service);
        return callee is null ? name : $"{name} -> {callee}";
    }

    private static List<RawSpan> ReadSpans(JsonElement root)
    {
        var spans = new List<RawSpan>();

        // Tempo's v1 endpoint answers { "batches": [...] }; plain OTLP JSON uses "resourceSpans".
        if (!root.TryGetProperty("batches", out var batches) && !root.TryGetProperty("resourceSpans", out batches))
        {
            return spans;
        }

        foreach (var batch in batches.EnumerateArray())
        {
            var service = ReadAttributes(batch.GetProperty("resource")).GetValueOrDefault("service.name") ?? "unknown";
            if (!batch.TryGetProperty("scopeSpans", out var scopes) && !batch.TryGetProperty("instrumentationLibrarySpans", out scopes))
            {
                continue;
            }

            foreach (var scope in scopes.EnumerateArray())
            {
                foreach (var span in scope.GetProperty("spans").EnumerateArray())
                {
                    spans.Add(ReadSpan(service, span));
                }
            }
        }

        return spans;
    }

    private static RawSpan ReadSpan(string service, JsonElement span)
    {
        var attributes = ReadAttributes(span);
        var start = ParseNanos(span.GetProperty("startTimeUnixNano"));
        var end = ParseNanos(span.GetProperty("endTimeUnixNano"));
        string? statusCode = null, statusMessage = null;
        if (span.TryGetProperty("status", out var status))
        {
            statusCode = status.TryGetProperty("code", out var c) ? c.GetString() : null;
            statusMessage = status.TryGetProperty("message", out var m) ? m.GetString() : null;
        }

        return new RawSpan(
            Id: span.GetProperty("spanId").GetString()!,
            ParentId: span.TryGetProperty("parentSpanId", out var p) && p.GetString() is { Length: > 0 } parent ? parent : null,
            Service: service,
            Name: span.GetProperty("name").GetString() ?? string.Empty,
            Kind: span.TryGetProperty("kind", out var k) ? k.GetString() : null,
            StartNanos: start,
            EndNanos: end,
            Attributes: attributes,
            Error: DescribeError(statusCode, statusMessage, attributes));
    }

    /// <summary>
    /// The .NET instrumentation sets status ERROR without a message; the useful bits are <c>error.type</c>
    /// (exception type on server spans, the status code on client spans) and the HTTP status code.
    /// </summary>
    private static string? DescribeError(string? statusCode, string? statusMessage, Dictionary<string, string> attributes)
    {
        _ = int.TryParse(attributes.GetValueOrDefault("http.response.status_code"), CultureInfo.InvariantCulture, out var httpStatus);
        if (statusCode != "STATUS_CODE_ERROR" && httpStatus < 500)
        {
            return null;
        }

        var parts = new List<string>();
        if (attributes.TryGetValue("error.type", out var errorType) && errorType != httpStatus.ToString(CultureInfo.InvariantCulture))
        {
            parts.Add(errorType);
        }

        if (httpStatus > 0)
        {
            parts.Add($"HTTP {httpStatus}");
        }

        if (!string.IsNullOrWhiteSpace(statusMessage))
        {
            parts.Add(statusMessage.Length <= MaxErrorLength ? statusMessage : string.Concat(statusMessage.AsSpan(0, MaxErrorLength), "…"));
        }

        return parts.Count > 0 ? string.Join(", ", parts) : "error";
    }

    private static Dictionary<string, string> ReadAttributes(JsonElement owner)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!owner.TryGetProperty("attributes", out var attributes))
        {
            return result;
        }

        foreach (var a in attributes.EnumerateArray())
        {
            var value = a.GetProperty("value");
            // OTLP JSON: { "stringValue": "x" } | { "intValue": "42" } | { "boolValue": true } | { "doubleValue": 1.5 }
            foreach (var v in value.EnumerateObject())
            {
                result[a.GetProperty("key").GetString()!] = v.Value.ValueKind == JsonValueKind.String
                    ? v.Value.GetString()!
                    : v.Value.GetRawText();
                break;
            }
        }

        return result;
    }

    private static long ParseNanos(JsonElement e) =>
        e.ValueKind == JsonValueKind.String ? long.Parse(e.GetString()!, CultureInfo.InvariantCulture) : e.GetInt64();

    private sealed record RawSpan(
        string Id,
        string? ParentId,
        string Service,
        string Name,
        string? Kind,
        long StartNanos,
        long EndNanos,
        Dictionary<string, string> Attributes,
        string? Error)
    {
        public TimeSpan Duration => TimeSpan.FromTicks((EndNanos - StartNanos) / 100);
    }
}
