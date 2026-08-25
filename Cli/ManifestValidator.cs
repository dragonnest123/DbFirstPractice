using System.Text.Json.Nodes;
using Json.Schema;

namespace Cli;

public static class ManifestValidator
{
    private static readonly JsonSchema Schema = LoadSchema();

    private static JsonSchema LoadSchema()
    {
        using var stream = typeof(ManifestValidator).Assembly
            .GetManifestResourceStream("Cli.Schemas.action-manifest.schema.json")
            ?? throw new InvalidOperationException("embedded manifest schema not found");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }

    public static bool IsValid(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node is not null && Schema.Evaluate(node).IsValid;
        }
        catch
        {
            return false;
        }
    }
}