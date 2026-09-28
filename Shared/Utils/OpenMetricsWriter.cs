using System.Globalization;
using System.Text;

namespace Shared.Utils;

/// <summary>
/// Minimal OpenMetrics text renderer. Series names carry no identifier labels,
/// so a scrape can never grow unbounded cardinality.
/// </summary>
public sealed class MetricSample
{
    public required string Name { get; init; }
    public required string Type { get; init; }
    public required string Help { get; init; }
    public IReadOnlyList<string> Labels { get; init; } = Array.Empty<string>();
    public double Value { get; init; }
}

public static class OpenMetricsWriter
{
    public const string ContentType = "application/openmetrics-text; version=1.0.0; charset=utf-8";

    public static string Render(IEnumerable<MetricSample> samples)
    {
        var builder = new StringBuilder();
        foreach (var group in samples.GroupBy(s => s.Name).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var first = group.First();
            builder.Append("# HELP ").Append(group.Key).Append(' ').Append(first.Help).Append('\n');
            builder.Append("# TYPE ").Append(group.Key).Append(' ').Append(first.Type).Append('\n');
            var name = first.Type == "counter" ? group.Key + "_total" : group.Key;
            foreach (var sample in group)
            {
                builder.Append(name);
                if (sample.Labels.Count > 0)
                    builder.Append('{').Append(string.Join(",", sample.Labels)).Append('}');
                builder.Append(' ').Append(Format(sample.Value)).Append('\n');
            }
        }

        builder.Append("# EOF\n");
        return builder.ToString();
    }

    public static string Format(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "+Inf";
        if (double.IsNegativeInfinity(value))
            return "-Inf";
        if (value == Math.Floor(value) && Math.Abs(value) < 1e15)
            return ((long)value).ToString(CultureInfo.InvariantCulture);
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    public static string Label(string name, string value)
    {
        var escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n");
        return name + "=\"" + escaped + "\"";
    }
}
