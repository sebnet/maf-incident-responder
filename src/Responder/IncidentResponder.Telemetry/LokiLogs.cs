using System.Text.Json;
using System.Text.RegularExpressions;
using IncidentResponder.Core;

namespace IncidentResponder.Telemetry;

/// <summary>
/// Loki side of <see cref="LgtmTelemetryQuery"/>: builds the LogQL query and turns the raw streams into a
/// <see cref="LogSummary"/> (grouped, counted, top-N). See docs/notes/lgtm-labels.md for the labels used.
/// </summary>
internal static partial class LokiLogs
{
    /// <summary>Loki's default <c>max_entries_limit_per_query</c>. Counts are a lower bound above it.</summary>
    public const int MaxLines = 5000;

    private const int MaxMessageLength = 300;
    private const int MaxTop = 50;

    // detected_level values Loki derives, from least to most severe.
    private static readonly string[] Levels = ["trace", "debug", "info", "warn", "error", "critical", "fatal"];

    public static string BuildQuery(LogQuery query)
    {
        var levels = Levels.AsSpan(LevelIndex(query.MinLevel)).ToArray();
        var logQl = $"{{service_name=\"{Escape(query.Service)}\"}} | detected_level=~\"{string.Join('|', levels)}\"";
        if (!string.IsNullOrWhiteSpace(query.Contains))
        {
            logQl += $" |= \"{Escape(query.Contains)}\"";
        }

        return logQl;
    }

    public static LogSummary Summarize(JsonElement root, LogQuery query)
    {
        var groups = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);
        var total = 0;

        foreach (var stream in root.GetProperty("data").GetProperty("result").EnumerateArray())
        {
            var streamLabels = ReadLabels(stream.GetProperty("stream"));
            foreach (var value in stream.GetProperty("values").EnumerateArray())
            {
                // [ "<unix ns>", "<line>", { structured metadata }? ]. Without the categorize-labels flag Loki merges
                // structured metadata into the stream labels; the optional third element covers the other shape.
                var labels = streamLabels;
                if (value.GetArrayLength() > 2 && value[2].ValueKind == JsonValueKind.Object)
                {
                    labels = new Dictionary<string, string>(streamLabels, StringComparer.Ordinal);
                    foreach (var (k, v) in ReadLabels(value[2]))
                    {
                        labels[k] = v;
                    }
                }

                var entry = new Entry(FromUnixNanos(value[0].GetString()!), value[1].GetString() ?? string.Empty, labels);
                var key = GroupKey(entry);
                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = new GroupAccumulator();
                }

                group.Add(entry);
                total++;
            }
        }

        var top = groups.Values
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LastSeen)
            .Take(Math.Clamp(query.Top, 1, MaxTop))
            .Select(g => g.ToLogGroup())
            .ToList();

        return new LogSummary(query.Service, query.Window, total, top);
    }

    /// <summary>
    /// Lines with the same message template, exception type/message, request path and dependency are "the same
    /// error". Ids and numbers are normalized so /items/3 and /items/4 land in one group.
    /// </summary>
    private static string GroupKey(Entry e) => string.Join('\u001f',
        e.Labels.GetValueOrDefault("_OriginalFormat_") ?? Normalize(e.Line),
        e.Labels.GetValueOrDefault("exception_type") ?? string.Empty,
        Normalize(e.Labels.GetValueOrDefault("exception_message")),
        Normalize(e.Labels.GetValueOrDefault("RequestPath")),
        Normalize(e.Labels.GetValueOrDefault("Uri")));

    /// <summary>
    /// What the model reads for a group. The rendered line alone is often useless ("An unhandled exception has
    /// occurred..."): the exception and the dependency Uri are what point at the root cause. Stack traces are left
    /// out on purpose (size).
    /// </summary>
    private static string Describe(Entry e)
    {
        var parts = new List<string> { Truncate(e.Line) };
        if (e.Labels.TryGetValue("exception_type", out var exType))
        {
            parts.Add($"exception: {exType}: {Truncate(e.Labels.GetValueOrDefault("exception_message") ?? string.Empty)}");
        }

        if (e.Labels.TryGetValue("RequestPath", out var path))
        {
            parts.Add($"request: {Normalize(path)}");
        }

        if (e.Labels.TryGetValue("Uri", out var uri))
        {
            parts.Add($"dependency: {Normalize(uri)}");
        }

        return string.Join(" | ", parts);
    }

    private static int LevelIndex(string minLevel)
    {
        // Accept the .NET names too (Warning, Information): the model may use either.
        var level = minLevel.Trim() switch
        {
            var l when l.Equals("warning", StringComparison.OrdinalIgnoreCase) => "warn",
            var l when l.Equals("information", StringComparison.OrdinalIgnoreCase) => "info",
            var l => l,
        };
        var index = Array.FindIndex(Levels, l => l.Equals(level, StringComparison.OrdinalIgnoreCase));
        return index >= 0
            ? index
            : throw new ArgumentException($"Unknown log level '{minLevel}'. Use one of: {string.Join(", ", Levels)}.", nameof(minLevel));
    }

    private static Dictionary<string, string> ReadLabels(JsonElement obj)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind == JsonValueKind.String)
            {
                labels[p.Name] = p.Value.GetString()!;
            }
        }

        return labels;
    }

    internal static DateTimeOffset FromUnixNanos(string nanos) =>
        DateTimeOffset.UnixEpoch.AddTicks(long.Parse(nanos, System.Globalization.CultureInfo.InvariantCulture) / 100);

    internal static long ToUnixNanos(DateTimeOffset t) => (t - DateTimeOffset.UnixEpoch).Ticks * 100;

    /// <summary>LogQL string literal escaping.</summary>
    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string Truncate(string s) => s.Length <= MaxMessageLength ? s : string.Concat(s.AsSpan(0, MaxMessageLength), "…");

    private static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return string.Empty;
        }

        s = GuidPattern().Replace(s, "{guid}");
        s = HexPattern().Replace(s, "{hex}");
        return NumberPattern().Replace(s, "{n}");
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"\b[0-9a-fA-F]{16,}\b")]
    private static partial Regex HexPattern();

    [GeneratedRegex(@"\d+")]
    private static partial Regex NumberPattern();

    private sealed record Entry(DateTimeOffset At, string Line, IReadOnlyDictionary<string, string> Labels);

    private sealed class GroupAccumulator
    {
        private Entry? _newest;
        private string? _sampleTraceId;

        public int Count { get; private set; }
        public DateTimeOffset FirstSeen { get; private set; } = DateTimeOffset.MaxValue;
        public DateTimeOffset LastSeen { get; private set; } = DateTimeOffset.MinValue;

        public void Add(Entry e)
        {
            Count++;
            if (e.At < FirstSeen)
            {
                FirstSeen = e.At;
            }

            if (e.At >= LastSeen)
            {
                LastSeen = e.At;
                _newest = e;
                // Prefer the newest trace id: most likely still in Tempo and representative of the current state.
                _sampleTraceId = e.Labels.GetValueOrDefault("trace_id") ?? _sampleTraceId;
            }
            else
            {
                _sampleTraceId ??= e.Labels.GetValueOrDefault("trace_id");
            }
        }

        public LogGroup ToLogGroup() => new(Describe(_newest!), Count, FirstSeen, LastSeen, _sampleTraceId);
    }
}
