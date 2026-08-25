using System.Text.Json;
using Npgsql;

namespace Cli;

public sealed class CatalogStore
{
    private readonly string _connStr;

    public CatalogStore()
    {
        _connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? "Host=postgres;Port=5432;Database=course;Username=course_migration;Password=migration;Include Error Detail=false";
    }

    private const string SelectColumns =
        "module, action, version, http_method, target_schema, target_function, " +
        "request_schema::text, response_schema::text, outcomes::text, required_policy::text, " +
        "idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version";

    public async Task<Manifest?> FindManifestAsync(string module, string action, int version)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM api.action_catalog WHERE module=@m AND action=@a AND version=@v", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("v", version);
        var row = await ReadRowAsync(cmd);
        return row;
    }

    public async Task<List<Manifest>> GetRouteAsync(string module, string action)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM api.action_catalog WHERE module=@m AND action=@a", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        return await ReadRowsAsync(cmd);
    }

    public async Task<bool> HasDefaultAsync(string module, string action)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS(SELECT 1 FROM api.action_catalog WHERE module=@m AND action=@a AND is_default)", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task InsertManifestAsync(Manifest m)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, " +
            "request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, " +
            "timeout_ms, enabled, is_default, contract_version) " +
            "VALUES(@m,@a,@v,@http,@ts,@tf,@req::jsonb,@resp::jsonb,@outcomes::jsonb,@policy::jsonb,@idem_mode,@idem_scope,@timeout,@enabled,@is_default,@cv)", conn);
        cmd.Parameters.AddWithValue("m", m.Module);
        cmd.Parameters.AddWithValue("a", m.Action);
        cmd.Parameters.AddWithValue("v", m.Version);
        cmd.Parameters.AddWithValue("http", m.HttpMethod);
        cmd.Parameters.AddWithValue("ts", m.TargetSchema);
        cmd.Parameters.AddWithValue("tf", m.TargetFunction);
        cmd.Parameters.AddWithValue("req", m.RequestSchema.GetRawText());
        cmd.Parameters.AddWithValue("resp", m.ResponseSchema.GetRawText());
        cmd.Parameters.AddWithValue("outcomes", JsonSerializer.Serialize(m.Outcomes));
        cmd.Parameters.AddWithValue("policy", JsonSerializer.Serialize(m.RequiredPolicy));
        cmd.Parameters.AddWithValue("idem_mode", m.IdempotencyMode);
        cmd.Parameters.AddWithValue("idem_scope", m.IdempotencyScope);
        cmd.Parameters.AddWithValue("timeout", m.TimeoutMs);
        cmd.Parameters.AddWithValue("enabled", m.Enabled);
        cmd.Parameters.AddWithValue("is_default", m.IsDefault);
        cmd.Parameters.AddWithValue("cv", m.ContractVersion);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ActivateAsync(string module, string action, int version)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var clear = new NpgsqlCommand(
            "UPDATE api.action_catalog SET is_default = false WHERE module=@m AND action=@a",
            conn, tx);
        clear.Parameters.AddWithValue("m", module);
        clear.Parameters.AddWithValue("a", action);
        await clear.ExecuteNonQueryAsync();
        await using var set = new NpgsqlCommand(
            "UPDATE api.action_catalog SET enabled = true, is_default = true WHERE module=@m AND action=@a AND version=@v",
            conn, tx);
        set.Parameters.AddWithValue("m", module);
        set.Parameters.AddWithValue("a", action);
        set.Parameters.AddWithValue("v", version);
        await set.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task DisableAsync(string module, string action, int version, int? replacement)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        if (replacement.HasValue)
        {
            await using var clear = new NpgsqlCommand(
                "UPDATE api.action_catalog SET is_default = false WHERE module=@m AND action=@a",
                conn, tx);
            clear.Parameters.AddWithValue("m", module);
            clear.Parameters.AddWithValue("a", action);
            await clear.ExecuteNonQueryAsync();
            await using var set = new NpgsqlCommand(
                "UPDATE api.action_catalog SET enabled = true, is_default = true WHERE module=@m AND action=@a AND version=@r",
                conn, tx);
            set.Parameters.AddWithValue("m", module);
            set.Parameters.AddWithValue("a", action);
            set.Parameters.AddWithValue("r", replacement.Value);
            await set.ExecuteNonQueryAsync();
        }
        await using var off = new NpgsqlCommand(
            "UPDATE api.action_catalog SET enabled = false WHERE module=@m AND action=@a AND version=@v",
            conn, tx);
        off.Parameters.AddWithValue("m", module);
        off.Parameters.AddWithValue("a", action);
        off.Parameters.AddWithValue("v", version);
        await off.ExecuteNonQueryAsync();
        await tx.CommitAsync();
    }

    public async Task<List<(string Module, string Action, int Version)>> ListAllAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT module, action, version FROM api.action_catalog ORDER BY module, action, version", conn);
        await using var r = await cmd.ExecuteReaderAsync();
        var items = new List<(string, string, int)>();
        while (await r.ReadAsync())
            items.Add((r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return items;
    }

    public async Task<string?> GetMigrationChecksumAsync(string filename)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT checksum FROM public.schema_migrations WHERE filename=@f", conn);
        cmd.Parameters.AddWithValue("f", filename);
        return (string?)await cmd.ExecuteScalarAsync();
    }

    public async Task ApplyMigrationAsync(string filename, string checksum, string sql)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync();
            await using var ins = new NpgsqlCommand(
                "INSERT INTO public.schema_migrations(filename, checksum) VALUES(@f,@c)", conn, tx);
            ins.Parameters.AddWithValue("f", filename);
            ins.Parameters.AddWithValue("c", checksum);
            await ins.ExecuteNonQueryAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task GrantUsageToOwnerAsync()
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "DO $$ " +
            "DECLARE s text; " +
            "BEGIN " +
            "FOR s IN SELECT n.nspname FROM pg_namespace n JOIN pg_roles r ON r.oid = n.nspowner WHERE r.rolname = current_user " +
            "LOOP EXECUTE format('GRANT USAGE ON SCHEMA %I TO course_owner', s); END LOOP; " +
            "END $$;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<Manifest?> ReadRowAsync(NpgsqlCommand cmd)
    {
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return Manifest.FromRow(JsonDocument.Parse(RowToJson(r)).RootElement);
    }

    private static async Task<List<Manifest>> ReadRowsAsync(NpgsqlCommand cmd)
    {
        await using var r = await cmd.ExecuteReaderAsync();
        var list = new List<Manifest>();
        while (await r.ReadAsync())
            list.Add(Manifest.FromRow(JsonDocument.Parse(RowToJson(r)).RootElement));
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
            if (i > 0) sb.Append(',');
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