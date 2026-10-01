using System.Net.Sockets;
using Npgsql;
using Shared.Utils;

namespace Shared.Services;

/// <summary>
/// Bounded retry for opening a connection. A container that is still starting up
/// refuses or drops the TCP connection, and that is a transport fault worth
/// retrying. A server that answered and rejected the command is not: SQL errors
/// from migrations, policies or queries surface immediately so a real fault is
/// never hidden behind a retry budget.
/// </summary>
public static class PostgresConnect
{
    public const int DefaultAttempts = 60;
    public const int DefaultDelayMs = 1000;

    /// <summary>
    /// True only for transport level faults. <see cref="PostgresException"/> is the
    /// server answering, so it is deliberately never treated as transient.
    /// </summary>
    public static bool IsTransient(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException)
                return false;

            if (current is SocketException or TimeoutException or IOException)
                return true;

            if (current is NpgsqlException { IsTransient: true })
                return true;

            if (current.InnerException is null)
                return false;
        }

        return false;
    }

    /// <summary>
    /// Opens a connection, retrying only transport faults until <paramref name="attempts"/>
    /// is exhausted. The caller owns the returned connection.
    /// </summary>
    public static Task<NpgsqlConnection> OpenAsync(
        string connectionString,
        int attempts = DefaultAttempts,
        int delayMs = DefaultDelayMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(connectionString);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        return OpenCoreAsync(connectionString, attempts, delayMs, cancellationToken);
    }

    private static async Task<NpgsqlConnection> OpenCoreAsync(
        string connectionString,
        int attempts,
        int delayMs,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var connection = new NpgsqlConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken);
                return connection;
            }
            catch (Exception error) when (IsTransient(error) && attempt < attempts)
            {
                await connection.DisposeAsync();
                StructuredLog.Write("postgres.connect_retry", new Dictionary<string, object?>
                {
                    ["attempt"] = attempt,
                    ["maxAttempts"] = attempts,
                    ["errorType"] = error.GetType().Name
                });

                if (delayMs > 0)
                    await Task.Delay(delayMs, cancellationToken);
            }
            catch
            {
                await connection.DisposeAsync();
                throw;
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="operation"/> against a connection opened with
    /// <see cref="OpenAsync(string,int,int,CancellationToken)"/>.
    /// </summary>
    public static async Task<T> RunAsync<T>(
        Func<NpgsqlConnection, Task<T>> operation,
        string connectionString,
        int attempts = DefaultAttempts,
        int delayMs = DefaultDelayMs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        await using var connection = await OpenAsync(connectionString, attempts, delayMs, cancellationToken);
        return await operation(connection);
    }
}