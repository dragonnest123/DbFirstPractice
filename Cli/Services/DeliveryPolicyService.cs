using Cli.Utils;
using Npgsql;
using Shared.Services;

namespace Cli.Services;

/// <summary>
/// Applies the runtime delivery policy. The function is a plain upsert, so the
/// command is safe to run on every start of the CLI service.
/// </summary>
public sealed class DeliveryPolicyService
{
    public sealed record Policy(
        int MaxAttempts,
        int BackoffBaseMs,
        int BackoffMaxMs,
        int JitterMaxMs,
        int LeaseMs);

    private readonly string _connStr;

    public DeliveryPolicyService(string? connectionString = null)
    {
        _connStr = connectionString
            ?? Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? "Host=postgres;Port=5432;Database=course;Username=course_migration;Password=migration;Include Error Detail=false";
    }

    public static Policy FromEnvironment() => new(
        Read("COURSE_OUTBOX_MAX_ATTEMPTS", 4, 1),
        Read("COURSE_OUTBOX_BACKOFF_BASE_MS", 200, 0),
        Read("COURSE_OUTBOX_BACKOFF_MAX_MS", 800, 0),
        Read("COURSE_OUTBOX_JITTER_MAX_MS", 100, 0),
        Read("COURSE_OUTBOX_LEASE_MS", 2000, 1));

    public async Task<System.Text.Json.JsonElement> ApplyAsync(Policy policy)
    {
        await using var conn = await PostgresConnect.OpenAsync(_connStr);

        await using var cmd = new NpgsqlCommand(
            "SELECT delivery.apply_outbox_policy(@a,@b,@c,@d,@e)", conn);
        cmd.Parameters.AddWithValue("a", policy.MaxAttempts);
        cmd.Parameters.AddWithValue("b", policy.BackoffBaseMs);
        cmd.Parameters.AddWithValue("c", policy.BackoffMaxMs);
        cmd.Parameters.AddWithValue("d", policy.JitterMaxMs);
        cmd.Parameters.AddWithValue("e", policy.LeaseMs);

        var result = await cmd.ExecuteScalarAsync();
        return System.Text.Json.JsonDocument.Parse((string)result!).RootElement.Clone();
    }

    private static int Read(string name, int fallback, int minimum)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var parsed) && parsed >= minimum ? parsed : fallback;
    }
}
