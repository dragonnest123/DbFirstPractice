using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cli;

public sealed record Manifest(
    string ContractVersion,
    string Module,
    string Action,
    int Version,
    string HttpMethod,
    string TargetSchema,
    string TargetFunction,
    JsonElement RequestSchema,
    JsonElement ResponseSchema,
    string[] Outcomes,
    string[] RequiredPolicy,
    string IdempotencyMode,
    string IdempotencyScope,
    int TimeoutMs,
    bool Enabled,
    bool IsDefault)
{
    public string Key => $"{Module}.{Action}";

    public bool SameAs(Manifest other) =>
        ContractVersion == other.ContractVersion
        && Module == other.Module
        && Action == other.Action
        && Version == other.Version
        && HttpMethod == other.HttpMethod
        && TargetSchema == other.TargetSchema
        && TargetFunction == other.TargetFunction
        && JsonEquals(RequestSchema, other.RequestSchema)
        && JsonEquals(ResponseSchema, other.ResponseSchema)
        && Outcomes.SequenceEqual(other.Outcomes)
        && RequiredPolicy.SequenceEqual(other.RequiredPolicy)
        && IdempotencyMode == other.IdempotencyMode
        && IdempotencyScope == other.IdempotencyScope
        && TimeoutMs == other.TimeoutMs
        && Enabled == other.Enabled
        && IsDefault == other.IsDefault;

    private static bool JsonEquals(JsonElement a, JsonElement b) =>
        JsonNode.DeepEquals(JsonNode.Parse(a.GetRawText()), JsonNode.Parse(b.GetRawText()));

    public static bool TryLoad(string path, out Manifest? manifest, out string? error)
    {
        manifest = null;
        error = null;
        try
        {
            var text = File.ReadAllText(path);
            var root = JsonDocument.Parse(text).RootElement;
            manifest = new Manifest(
                root.GetProperty("contract_version").GetString()!,
                root.GetProperty("module").GetString()!,
                root.GetProperty("action").GetString()!,
                root.GetProperty("version").GetInt32(),
                root.GetProperty("http_method").GetString()!,
                root.GetProperty("target_schema").GetString()!,
                root.GetProperty("target_function").GetString()!,
                root.GetProperty("request_schema").Clone(),
                root.GetProperty("response_schema").Clone(),
                root.GetProperty("outcomes").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                root.GetProperty("required_policy").EnumerateArray().Select(x => x.GetString()!).ToArray(),
                root.GetProperty("idempotency_mode").GetString()!,
                root.GetProperty("idempotency_scope").GetString()!,
                root.GetProperty("timeout_ms").GetInt32(),
                root.GetProperty("enabled").GetBoolean(),
                root.GetProperty("is_default").GetBoolean());
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static Manifest FromRow(JsonElement row) =>
        new(
            row.GetProperty("contract_version").GetString()!,
            row.GetProperty("module").GetString()!,
            row.GetProperty("action").GetString()!,
            row.GetProperty("version").GetInt32(),
            row.GetProperty("http_method").GetString()!,
            row.GetProperty("target_schema").GetString()!,
            row.GetProperty("target_function").GetString()!,
            row.GetProperty("request_schema").Clone(),
            row.GetProperty("response_schema").Clone(),
            row.GetProperty("outcomes").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            row.GetProperty("required_policy").EnumerateArray().Select(x => x.GetString()!).ToArray(),
            row.GetProperty("idempotency_mode").GetString()!,
            row.GetProperty("idempotency_scope").GetString()!,
            row.GetProperty("timeout_ms").GetInt32(),
            row.GetProperty("enabled").GetBoolean(),
            row.GetProperty("is_default").GetBoolean());
}