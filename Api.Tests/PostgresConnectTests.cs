using System.Net.Sockets;
using Npgsql;
using Shared.Services;
using Xunit;

namespace Api.Tests;

/// <summary>
/// The connect retry exists for a container that is not listening yet. It must never
/// absorb a server that answered and refused the work: a failing migration has to
/// reach the caller on the first attempt.
/// </summary>
public class PostgresConnectTests
{
    [Fact]
    public void SocketFaultIsTransient()
    {
        Assert.True(PostgresConnect.IsTransient(new SocketException((int)SocketError.ConnectionRefused)));
    }

    [Fact]
    public void TimeoutIsTransient()
    {
        Assert.True(PostgresConnect.IsTransient(new TimeoutException()));
    }

    [Fact]
    public void NpgsqlTransientIsTransient()
    {
        Assert.True(PostgresConnect.IsTransient(new NpgsqlException("connection reset", new SocketException())));
    }

    [Fact]
    public void ServerSqlErrorIsNotTransient()
    {
        Assert.False(PostgresConnect.IsTransient(SqlError("relation does not exist")));
    }

    [Fact]
    public void SqlErrorWrappedInTransportFaultIsNotTransient()
    {
        var error = new InvalidOperationException("wrapped", SqlError("syntax error"));

        Assert.False(PostgresConnect.IsTransient(error));
    }

    private static PostgresException SqlError(string message) =>
        new(message, "ERROR", "ERROR", "42P01");

    [Fact]
    public void UnrelatedErrorIsNotTransient()
    {
        Assert.False(PostgresConnect.IsTransient(new InvalidOperationException("bad input")));
    }

    [Fact]
    public async Task RetryBudgetIsBoundedAndThenReported()
    {
        // Port 1 is reserved and nothing listens there. The helper may retry a
        // refused connection, but it must give up and report instead of hanging.
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(() => PostgresConnect.OpenAsync(
            "Host=127.0.0.1;Port=1;Database=course;Username=u;Password=p;Timeout=1",
            attempts: 3,
            delayMs: 50));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
            $"expected the retry budget to be bounded, took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task MalformedConnectionStringIsNotRetried()
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<Exception>(() => PostgresConnect.OpenAsync(
            "this is not a connection string", attempts: 5, delayMs: 1000));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2),
            $"expected no retry for a configuration fault, took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task NonPositiveAttemptBudgetIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PostgresConnect.OpenAsync("Host=127.0.0.1", attempts: 0));
    }
}