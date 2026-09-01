using System.Text.Json;
using Npgsql;
using Shared.Models;

namespace Shared.Services;

public sealed class ActionCatalogService
{
    private const string SelectColumns =
        "module, action, version, http_method, target_schema, target_function, " +
        "request_schema::text, response_schema::text, outcomes::text, required_policy::text, " +
        "idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version";

    public string ConnectionString { get; }

    public ActionCatalogService(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public bool IsReady()
    {
        try
        {
            using var conn = new NpgsqlConnection(ConnectionString);
            conn.Open();

            using var cmd = new NpgsqlCommand("SELECT 1", conn);
            cmd.ExecuteScalar();

            using var cmd2 = new NpgsqlCommand("SELECT count(*) FROM api.action_catalog LIMIT 1", conn);
            cmd2.ExecuteScalar();

            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<ActionManifest?> GetOrDefault(string module, string action, int? explicitVersion)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();

        var sql = explicitVersion.HasValue
            ? $"SELECT {SelectColumns} FROM api.action_catalog WHERE module=@m AND action=@a AND version=@v AND enabled=true"
            : $"SELECT {SelectColumns} FROM api.action_catalog WHERE module=@m AND action=@a AND is_default=true AND enabled=true";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        if (explicitVersion.HasValue)
            cmd.Parameters.AddWithValue("v", explicitVersion.Value);

        return await ReadRowAsync(cmd);
    }

    public async Task<List<ActionManifest>> GetRouteAsync(string module, string action)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM api.action_catalog WHERE module=@m AND action=@a", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        
        return await ReadRowsAsync(cmd);
    }

    public async Task<List<(string Module, string Action, int Version)>> ListAllAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        
        await using var cmd = new NpgsqlCommand(
            "SELECT module, action, version FROM api.action_catalog ORDER BY module, action, version", conn);
        
        await using var r = await cmd.ExecuteReaderAsync();
        
        var items = new List<(string, string, int)>();
        while (await r.ReadAsync())
            items.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
        
        return items;
    }

    private static async Task<ActionManifest?> ReadRowAsync(NpgsqlCommand cmd)
    {
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            return null;
        
        return ActionManifest.FromRow(JsonDocument.Parse(RowToJson(r)).RootElement);
    }

    private static async Task<List<ActionManifest>> ReadRowsAsync(NpgsqlCommand cmd)
    {
        await using var r = await cmd.ExecuteReaderAsync();
        
        var list = new List<ActionManifest>();
        while (await r.ReadAsync())
            list.Add(ActionManifest.FromRow(JsonDocument.Parse(RowToJson(r)).RootElement));
        
        return list;
    }

    private static string RowToJson(NpgsqlDataReader r)
    {
        var columns = new[]
        {
            "module", "action", "version", "http_method", "target_schema", "target_function",
            "request_schema", "response_schema", "outcomes", "required_policy",
            "idempotency_mode", "idempotency_scope", "timeout_ms", "enabled", "is_default", "contract_version"
        };
        var jsonb = new HashSet<string> { "request_schema", "response_schema", "outcomes", "required_policy" };
        var sb = new System.Text.StringBuilder("{");
        
        for (var i = 0; i < columns.Length; i++)
        {
            if (i > 0) 
                sb.Append(',');
            
            sb.Append('"').Append(columns[i]).Append("\":");
            
            var value = r.IsDBNull(i) ? null : r.GetValue(i);
            
            sb.Append(jsonb.Contains(columns[i]) && value is string s
                ? JsonDocument.Parse(s).RootElement.GetRawText()
                : JsonSerializer.Serialize(value));
        }
        
        sb.Append('}');
        
        return sb.ToString();
    }
}
