using Npgsql;

namespace Cli.Services;

public sealed class PublicationException : Exception
{
    public string Code { get; }

    public PublicationException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public sealed class PublicationService
{
    private static readonly string[] KnownCodes =
        ["manifest.conflict", "manifest.invalid", "action.not_found", "action.invalid"];

    private readonly string _connStr;

    public PublicationService(string connectionString)
    {
        _connStr = connectionString;
    }

    public async Task PublishAsync(string manifestJson)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT api.publish_action(@m::jsonb)::text", conn);
        cmd.Parameters.AddWithValue("m", manifestJson);

        await ExecuteAsync(cmd);
    }

    public async Task ActivateAsync(string module, string action, int version)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT api.activate_action(@m,@a,@v)::text", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("v", version);

        await ExecuteAsync(cmd);
    }

    public async Task DisableAsync(string module, string action, int version, int? replacement)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT api.disable_action(@m,@a,@v,@r)::text", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("v", version);
        cmd.Parameters.AddWithValue("r", (object?)replacement ?? DBNull.Value);

        await ExecuteAsync(cmd);
    }

    private static async Task ExecuteAsync(NpgsqlCommand cmd)
    {
        try
        {
            await cmd.ExecuteScalarAsync();
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

            throw new PublicationException("publication.failed", text);
        }
    }
}