using Npgsql;

namespace Cli.Services;

public sealed class MigrationService
{
    private readonly string _connStr;

    public MigrationService()
    {
        _connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? "Host=postgres;Port=5432;Database=course;Username=course_migration;Password=migration;Include Error Detail=false";
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
}