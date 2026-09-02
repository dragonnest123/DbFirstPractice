using Npgsql;

namespace Cli.Services;

public sealed class FlowService
{
    private static readonly string[] KnownCodes =
    [
        "manifest.conflict", "manifest.invalid",
        "flow.invalid", "flow.not_found", "flow.inactive",
        "workflow.start_conflict", "workflow.process_not_found",
        "workflow.lease_stale", "workflow.unknown_outcome",
        "workflow.claim_invalid", "workflow.append_only",
        "signal.invalid", "signal.unknown", "signal.conflict",
        "internal.error"
    ];

    private readonly string _connStr;

    public FlowService(string connectionString)
    {
        _connStr = connectionString;
    }

    public Task<string> PublishAsync(string mapJson)
    {
        return ExecuteAsync("SELECT workflow.publish_flow(@m::jsonb)::text",
            ("m", mapJson));
    }

    public Task<string> ActivateAsync(string flow, int version)
    {
        return ExecuteAsync("SELECT workflow.activate_flow(@f,@v)::text",
            ("f", flow), ("v", version));
    }

    public Task<string> StartAsync(string flow, string businessKey, string dataJson)
    {
        return ExecuteAsync("SELECT workflow.start_process(@f,@k,@d::jsonb)::text",
            ("f", flow), ("k", businessKey), ("d", dataJson));
    }

    public Task<string> SignalAsync(string processId, string type, string messageId, string payloadJson)
    {
        return ExecuteAsync("SELECT workflow.accept_signal(@p::uuid,@t,@m,@b::jsonb)::text",
            ("p", processId), ("t", type), ("m", messageId), ("b", payloadJson));
    }

    public Task<string> GetAsync(string processId)
    {
        return ExecuteAsync("SELECT workflow.get_process(@p::uuid)::text",
            ("p", processId));
    }

    public Task<string> TestFinishAsync(string jobId, string owner, long leaseVersion, string outcome, string resultJson)
    {
        return ExecuteAsync("SELECT workflow.finish_job(@j::uuid,@o,@lv,@oc,@r::jsonb)::text",
            ("j", jobId), ("o", owner), ("lv", leaseVersion), ("oc", outcome), ("r", resultJson));
    }

    public async Task<List<Dictionary<string, object?>>> ListAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT flow_name, flow_version, status, is_active, published_at FROM autocheck.flow_versions " +
            "ORDER BY flow_name, flow_version", conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        var items = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            items.Add(new Dictionary<string, object?>
            {
                ["flowName"] = reader.GetString(0),
                ["flowVersion"] = reader.GetInt32(1),
                ["status"] = reader.GetString(2),
                ["isActive"] = reader.GetBoolean(3),
                ["publishedAt"] = reader.GetFieldValue<DateTime>(4)
            });
        }
        return items;
    }

    private async Task<string> ExecuteAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);

        try
        {
            return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
        }
        catch (NpgsqlException ex)
        {
            var text = ex is PostgresException postgres ? postgres.MessageText : ex.Message;
            if (text.StartsWith("ERROR: ", StringComparison.Ordinal))
                text = text[7..];

            var sep = text.IndexOf(": ", StringComparison.Ordinal);
            if (sep > 0 && KnownCodes.Contains(text[..sep]))
            {
                var code = text[..sep];
                throw new PublicationException(code, text[(sep + 2)..]);
            }

            throw new PublicationException("workflow.failed", text);
        }
    }
}