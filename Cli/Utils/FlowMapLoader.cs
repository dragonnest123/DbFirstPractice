using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace Cli.Utils;

public static class FlowMapLoader
{
    public static JsonNode? Load(string path)
    {
        var text = File.ReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (path.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
            return ParseYaml(text);

        return JsonNode.Parse(text);
    }

    private static JsonNode? ParseYaml(string text)
    {
        var stream = new YamlStream();
        using var reader = new StringReader(text);
        stream.Load(reader);
        if (stream.Documents.Count == 0)
            return null;

        var converted = ToJson(stream.Documents[0].RootNode);
        return converted is null ? null : JsonNode.Parse(JsonSerializer.Serialize(converted));
    }

    private static object? ToJson(YamlNode node)
    {
        switch (node)
        {
            case YamlScalarNode scalar:
                if (scalar.Value is null)
                    return null;
                if (bool.TryParse(scalar.Value, out var boolean))
                    return boolean;
                if (long.TryParse(scalar.Value, out var integer))
                    return integer;
                if (double.TryParse(scalar.Value, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    return number;
                return scalar.Value;

            case YamlSequenceNode sequence:
                return sequence.Children.Select(ToJson).ToArray();

            case YamlMappingNode mapping:
                var result = new Dictionary<string, object?>();
                foreach (var pair in mapping.Children)
                {
                    if (pair.Key is YamlScalarNode key && key.Value is not null)
                        result[key.Value] = ToJson(pair.Value);
                }
                return result;

            default:
                return null;
        }
    }
}