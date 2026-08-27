using Api.Dto;
using Api.Utils;
using Npgsql;
using Shared.Models;

namespace Api.Services;

public sealed class IdempotencyService
{
    private readonly ILogger<IdempotencyService> _logger;

    public IdempotencyService(ILogger<IdempotencyService> logger)
    {
        _logger = logger;
    }

    public static string? BuildScopeKey(ActionManifest entry, string principal, string consumer, string module, string action)
        => entry.IdempotencyScope switch
        {
            "principal_action" => $"{principal}:{module}.{action}",
            "consumer_action" => $"{consumer}:{module}.{action}",
            "global_action" => $"{module}.{action}",
            _ => null
        };

public async Task ClaimAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string scopeKey, string requestId, string payloadHash, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO api.idempotency_store(scope_key, request_id, payload_hash, response) VALUES(@k,@r,@h,NULL)",
            conn, tx);
        cmd.Parameters.AddWithValue("k", scopeKey);
        cmd.Parameters.AddWithValue("r", requestId);
        cmd.Parameters.AddWithValue("h", payloadHash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IResult?> ClaimOrReplayAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, RequestState s, CancellationToken ct)
    {
        var scopeKey = s.IdempotencyScopeKey!;
        try
        {
            await ClaimAsync(conn, tx, scopeKey, s.RequestId, s.PayloadHash, ct);
            return null;
        }
        catch (PostgresException ex) when (ex.SqlState == "23505")
        {
            await tx.RollbackAsync(ct);

            var existing = await ReadAsync(conn, scopeKey, s.RequestId);
            if (existing is null)
                return Envelope.Error(
                    "idempotency.conflict",
                    "same key different payload",
                    false,
                    s.CorrelationId,
                    s.Version,
                    409);

            return existing.Value.Hash == s.PayloadHash && existing.Value.Response.Length > 0
                ? Envelope.OkFromStored(existing.Value.Response)
                : Envelope.Error(
                    "idempotency.conflict",
                    "same key different payload",
                    false,
                    s.CorrelationId,
                    s.Version,
                    409);
        }
    }

    public async Task<(string Hash, string Response)?> ReadAsync(NpgsqlConnection conn, string scopeKey, string requestId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT payload_hash, response FROM api.idempotency_store WHERE scope_key=@k AND request_id=@r",
            conn);
        cmd.Parameters.AddWithValue("k", scopeKey);
        cmd.Parameters.AddWithValue("r", requestId);
        
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) 
            return null;
        
        var hash = reader.GetString(0);
        var response = reader.IsDBNull(1) ? null : reader.GetString(1);
        
        return (hash, response ?? "");
    }

    public async Task StoreResponseAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string scopeKey, string requestId, string responseJson, CancellationToken ct)
    {
        try
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE api.idempotency_store SET response = @resp::jsonb WHERE scope_key=@k AND request_id=@r",
                conn, tx);
            cmd.Parameters.AddWithValue("resp", responseJson);
            cmd.Parameters.AddWithValue("k", scopeKey);
            cmd.Parameters.AddWithValue("r", requestId);
            
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist idempotency response for key {ScopeKey}:{RequestId}", scopeKey, requestId);
        }
    }
}