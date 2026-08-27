using Npgsql;
using Shared.Services;

namespace Api.Services;

public sealed class DispatchService
{
    private readonly string _connStr;
    private readonly ILogger<DispatchService> _logger;

    public DispatchService(ActionCatalogService actionCatalog, ILogger<DispatchService> logger)
    {
        _connStr = actionCatalog.ConnectionString;
        _logger = logger;
    }

    public async Task LogAsync(string correlationId, string requestId, string module, string action, int version, string principal, string payloadHash, string status, string? outcome)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync();

            await WriteAsync(conn, null, true, correlationId, requestId, module, action, version, principal, payloadHash, status, outcome, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record dispatch for correlation {CorrelationId} ({Module}.{Action} v{Version}, status {Status})", correlationId, module, action, version, status);
        }
    }

    public async Task LogAsync(
        string correlationId, string requestId, string module, string action, int version,
        string principal, string payloadHash, string status, string? outcome,
        NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct)
    {
        try
        {
            await WriteAsync(conn, tx, false, correlationId, requestId, module, action, version, principal, payloadHash, status, outcome, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to record dispatch for correlation {CorrelationId} ({Module}.{Action} v{Version}, status {Status})", correlationId, module, action, version, status);
        }
    }

    private static async Task WriteAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, bool onConflictDoNothing,
        string correlationId, string requestId, string module, string action, int version,
        string principal, string payloadHash, string status, string? outcome, CancellationToken ct)
    {
        var sql = "INSERT INTO api.action_dispatches(correlation_id, request_id, module, action, version, principal, payload_hash, status, outcome) VALUES(@c,@r,@m,@a,@v,@p,@h,@s,@o)"
                  + (onConflictDoNothing ? " ON CONFLICT DO NOTHING" : "");
        await using var cmd = tx is null ? new NpgsqlCommand(sql, conn) : new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("c", Guid.Parse(correlationId));
        cmd.Parameters.AddWithValue("r", requestId);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("v", version);
        cmd.Parameters.AddWithValue("p", principal);
        cmd.Parameters.AddWithValue("h", payloadHash);
        cmd.Parameters.AddWithValue("s", status);
        cmd.Parameters.AddWithValue("o", (object?)outcome ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}