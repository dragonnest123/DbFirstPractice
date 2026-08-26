using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;

namespace Api.Utils;

public static partial class ValidationUtil
{
    public static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsValidSqlIdentifier(string value)
        => SqlIdentifier().IsMatch(value);

    public static bool IsValid(JsonElement schema, string json)
    {
        try
        {
            var compiled = JsonSchema.FromText(schema.GetRawText());
            var node = JsonNode.Parse(json);
            return compiled.Evaluate(node, new EvaluationOptions { RequireFormatValidation = true }).IsValid;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsValidResult(JsonElement schema, JsonElement result)
    {
        try
        {
            var compiled = JsonSchema.FromText(schema.GetRawText());
            var node = JsonNode.Parse(result.GetRawText());
            return compiled.Evaluate(node, new EvaluationOptions { RequireFormatValidation = true }).IsValid;
        }
        catch
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled)]
    private static partial Regex SqlIdentifier();
}