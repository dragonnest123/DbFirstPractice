using System.Text.Json;
using Api.Services;
using Api.Utils;

namespace Api.Dto;

public sealed record RequestState(
    string Module,
    string Action,
    string CorrelationId,
    string RequestId,
    string Principal,
    string PayloadHash,
    string ContextJson,
    string Payload,
    int? ExplicitVersion,
    int Version,
    int TimeoutMs,
    string ConnectionString,
    string? IdempotencyScopeKey)
{
    public static RequestState Build(
        string module,
        string action,
        string correlationId,
        string requestId,
        string principal,
        string consumer,
        string[] scopes,
        string payload,
        CatalogEntry entry,
        int? explicitVersion,
        string connectionString,
        string idempotencyKey,
        string? scopeKey)
    {
        var contextJson = JsonSerializer.Serialize(new
        {
            principal,
            consumer,
            scopes,
            correlationId,
            requestId,
            deadline = DateTime.UtcNow.AddMilliseconds(entry.TimeoutMs).ToString("o")
        });
        
        return new RequestState(
            module,
            action,
            correlationId,
            requestId,
            principal,
            HashUtil.Sha256Hex(payload),
            contextJson,
            payload,
            explicitVersion,
            entry.Version,
            entry.TimeoutMs,
            connectionString,
            entry.IdempotencyMode != "none" && !string.IsNullOrEmpty(idempotencyKey) ? scopeKey : null);
    }
}