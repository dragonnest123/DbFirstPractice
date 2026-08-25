using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Api.Services;

public static class SchemaValidator
{
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
}
