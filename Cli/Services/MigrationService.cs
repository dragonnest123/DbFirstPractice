using Npgsql;

namespace Cli.Services;

public sealed class MigrationService
{
    private const string FinalizeOwnershipSql = """
        DO $$
        DECLARE
            s text;
            t record;
            f record;
        BEGIN
            FOR s IN SELECT n.nspname FROM pg_namespace n JOIN pg_roles r ON r.oid = n.nspowner WHERE r.rolname = current_user
            LOOP
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO course_owner', s);
                EXECUTE format('GRANT USAGE, CREATE ON SCHEMA %I TO course_target', s);
            END LOOP;

            FOR t IN SELECT format('%I.%I', n.nspname, c.relname) AS q
                     FROM pg_class c
                     JOIN pg_namespace n ON n.oid = c.relnamespace
                     JOIN pg_roles r ON r.oid = c.relowner
                     WHERE r.rolname = current_user AND c.relkind IN ('r','p')
            LOOP
                EXECUTE format('GRANT ALL ON TABLE %s TO course_target', t.q);
            END LOOP;

            FOR t IN SELECT format('%I.%I', n.nspname, c.relname) AS q
                     FROM pg_class c
                     JOIN pg_namespace n ON n.oid = c.relnamespace
                     JOIN pg_roles r ON r.oid = c.relowner
                     WHERE r.rolname = current_user AND c.relkind = 'S'
            LOOP
                EXECUTE format('GRANT ALL ON SEQUENCE %s TO course_target', t.q);
            END LOOP;

            FOR f IN SELECT format('%I.%I(%s)', n.nspname, p.proname,
                            (SELECT string_agg(a.argtype::regtype::text, ', ' ORDER BY a.ord)
                             FROM unnest(p.proargtypes::oid[]) WITH ORDINALITY AS a(argtype, ord))) AS sig,
                             n.nspname AS ns
                     FROM pg_proc p
                     JOIN pg_namespace n ON n.oid = p.pronamespace
                     JOIN pg_roles r ON r.oid = p.proowner
                     WHERE r.rolname = current_user
                       AND p.pronargs = 2
                       AND p.proargtypes[0] = 'jsonb'::regtype::oid
                       AND p.proargtypes[1] = 'jsonb'::regtype::oid
                       AND p.prorettype = 'jsonb'::regtype::oid
            LOOP
                EXECUTE format('GRANT USAGE ON SCHEMA %I TO course_owner, course_target', f.ns);
                EXECUTE format('GRANT EXECUTE ON FUNCTION %s TO course_owner', f.sig);
                EXECUTE format('ALTER FUNCTION %s OWNER TO course_target', f.sig);
            END LOOP;
        END $$;
        """;

    private readonly string _connStr;

    public MigrationService(string? connectionString = null)
    {
        _connStr = connectionString
            ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
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

            await using var post = new NpgsqlCommand(FinalizeOwnershipSql, conn, tx);

            await post.ExecuteNonQueryAsync();

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
}