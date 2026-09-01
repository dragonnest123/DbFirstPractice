using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

public static class Db
{
    public static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (await cmd.ExecuteScalarAsync())?.ToString();
    }

    public static async Task<PostgresException?> TryExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        try
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException ex)
        {
            return ex;
        }
    }

    public static async Task<PostgresException> ExecErrorAsync(string connectionString, string sql)
    {
        var error = await TryExecAsync(connectionString, sql);
        Assert.NotNull(error);
        return error!;
    }
}