using System.Text.Json;
using Npgsql;

namespace Api.Services;

public sealed record CatalogEntry(
    int Version,
    JsonElement RequestSchema,
    JsonElement ResponseSchema,
    JsonElement Outcomes,
    JsonElement RequiredPolicy,
    string IdempotencyMode,
    string IdempotencyScope,
    int TimeoutMs
);

public sealed class CatalogService
{
    public string ConnectionString { get; }

    public CatalogService(IConfiguration cfg)
    {
        ConnectionString = cfg.GetConnectionString("CourseDb")
            ?? cfg["POSTGRES_CONNECTION"]
            ?? cfg["ConnectionStrings__CourseDb"]
            ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? "Host=postgres;Port=5432;Database=course;Username=course_runtime;Password=runtime;Include Error Detail=false";
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

    public async Task<CatalogEntry?> GetOrDefault(string module, string action, int? explicitVersion)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        
        NpgsqlCommand cmd = explicitVersion.HasValue
            ? new NpgsqlCommand("SELECT version, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms FROM api.action_catalog WHERE module=@m AND action=@a AND version=@v AND enabled=true", conn)
            : new NpgsqlCommand("SELECT version, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms FROM api.action_catalog WHERE module=@m AND action=@a AND is_default=true AND enabled=true", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        if (explicitVersion.HasValue) 
            cmd.Parameters.AddWithValue("v", explicitVersion.Value);
        
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) 
            return null;
        
        return new CatalogEntry(
            r.GetInt32(0),
            JsonDocument.Parse(r.GetString(1)).RootElement.Clone(),
            JsonDocument.Parse(r.GetString(2)).RootElement.Clone(),
            JsonDocument.Parse(r.GetString(3)).RootElement.Clone(),
            JsonDocument.Parse(r.GetString(4)).RootElement.Clone(),
            r.GetString(5),
            r.GetString(6),
            r.GetInt32(7)
        );
    }
}
