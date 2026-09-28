using Npgsql;
using Shared.Utils;

namespace Shared.Services;

/// <summary>
/// Publishes the business series from the read-only projection views. Only
/// counters and gauges over bounded state sets are exposed; identifiers stay in
/// the audit tables and in <c>diagnostics.trace</c>.
/// </summary>
public sealed class WorkflowMetricsService
{
    private const string ReadGaugesSql = """
        SELECT
          (SELECT count(*) FROM autocheck.jobs WHERE state = 'READY')::bigint,
          (SELECT COALESCE(EXTRACT(EPOCH FROM (clock_timestamp() - min(created_at))), 0)
             FROM autocheck.jobs WHERE state = 'READY')::double precision,
          (SELECT count(*) FROM autocheck.processes
            WHERE state IN ('WAITING_SIGNAL','WAITING_MANUAL'))::bigint,
          (SELECT count(*) FROM autocheck.outbox
            WHERE state IN ('PENDING','RETRY_WAIT','LEASED'))::bigint,
          (SELECT COALESCE(EXTRACT(EPOCH FROM (clock_timestamp() - min(created_at))), 0)
             FROM autocheck.outbox WHERE state IN ('PENDING','RETRY_WAIT','LEASED'))::double precision,
          (SELECT count(*) FROM autocheck.attempts WHERE status = 'FAILED')::bigint
        """;

    public string ConnectionString { get; }

    public WorkflowMetricsService(string connectionString)
    {
        ConnectionString = connectionString;
    }

    public IReadOnlyList<MetricSample> Collect()
    {
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = new NpgsqlCommand(ReadGaugesSql, conn);
        cmd.CommandTimeout = 5;
        using var reader = cmd.ExecuteReader();

        if (!reader.Read())
            throw new InvalidOperationException("metrics query returned no row");

        var samples = new List<MetricSample>
        {
            Gauge("workflow_jobs_ready", "Ready workflow jobs waiting to be claimed.", reader.GetInt64(0)),
            Gauge("workflow_job_oldest_age_seconds", "Age of the oldest ready workflow job.", reader.GetDouble(1)),
            Gauge("workflow_processes_waiting", "Workflow processes waiting for a signal or a manual decision.", reader.GetInt64(2)),
            Gauge("outbox_pending", "Outbox rows not yet delivered or confirmed.", reader.GetInt64(3)),
            Gauge("outbox_oldest_age_seconds", "Age of the oldest undelivered outbox row.", reader.GetDouble(4)),
            Counter("workflow_failures", "Workflow task attempts that failed.", reader.GetInt64(5))
        };
        return samples;
    }

    public IReadOnlyList<MetricSample> ProcessOnly(DateTimeOffset startedAt) =>
        new List<MetricSample>
        {
            Gauge("process_start_time_seconds", "Unix time the process started.", startedAt.ToUnixTimeMilliseconds() / 1000d)
        };

    private static MetricSample Gauge(string name, string help, double value) => new()
    {
        Name = name, Type = "gauge", Help = help, Value = value
    };

    private static MetricSample Counter(string name, string help, double value) => new()
    {
        Name = name, Type = "counter", Help = help, Value = value
    };
}
