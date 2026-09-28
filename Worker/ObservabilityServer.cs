using System.Net;
using System.Text;
using Npgsql;
using Shared.Utils;

namespace Workflow;

/// <summary>
/// Serves the liveness, readiness and OpenMetrics routes of a worker. Liveness
/// answers from the process alone; readiness additionally requires PostgreSQL,
/// so a database outage drains the worker without killing it.
/// </summary>
public sealed class ObservabilityServer : IDisposable
{
    private static readonly TimeSpan DependencyTtl = TimeSpan.FromSeconds(2);

    private readonly string _connectionString;
    private readonly string _service;
    private readonly HttpListener _listener = new();
    private readonly MetricsCounters _counters;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource _stopping = new();

    private DateTimeOffset _probedAt = DateTimeOffset.MinValue;
    private bool _databaseAvailable;

    public ObservabilityServer(string connectionString, string service, MetricsCounters counters, int port = 8080)
    {
        _connectionString = connectionString;
        _service = service;
        _counters = counters;
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public bool IsDatabaseAvailable()
    {
        if (DateTimeOffset.UtcNow - _probedAt <= DependencyTtl)
            return _databaseAvailable;

        try
        {
            using var conn = new NpgsqlConnection(_connectionString);
            conn.Open();
            using var cmd = new NpgsqlCommand("SELECT 1", conn) { CommandTimeout = 2 };
            cmd.ExecuteScalar();
            _databaseAvailable = true;
        }
        catch
        {
            _databaseAvailable = false;
        }

        _probedAt = DateTimeOffset.UtcNow;
        return _databaseAvailable;
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            try
            {
                var path = context.Request.Url?.AbsolutePath ?? "/";
                switch (path)
                {
                    case "/health/live":
                        await WriteJsonAsync(context, 200, $"{{\"status\":\"live\",\"service\":\"{_service}\"}}");
                        break;
                    case "/health/ready":
                        if (IsDatabaseAvailable())
                            await WriteJsonAsync(context, 200, $"{{\"status\":\"ready\",\"service\":\"{_service}\"}}");
                        else
                            await WriteJsonAsync(
                                context, 503,
                                "{\"status\":\"not_ready\",\"code\":\"dependency.unavailable\",\"dependencies\":[\"postgres\"]}");
                        break;
                    case "/metrics":
                        await WriteAsync(context, 200, OpenMetricsWriter.ContentType, Render());
                        break;
                    default:
                        await WriteJsonAsync(context, 404, "{\"status\":\"error\",\"code\":\"not.found\"}");
                        break;
                }
            }
            catch
            {
                // A failed probe response must not take the worker down.
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private string Render()
    {
        var samples = new List<MetricSample>
        {
            new()
            {
                Name = "process_start_time_seconds", Type = "gauge",
                Help = "Unix time the process started.",
                Value = _startedAt.ToUnixTimeMilliseconds() / 1000d
            },
            new()
            {
                Name = "workflow_jobs_claimed", Type = "counter",
                Help = "Workflow jobs claimed by this worker.", Value = _counters.Claimed
            },
            new()
            {
                Name = "workflow_jobs_completed", Type = "counter",
                Help = "Workflow jobs completed by this worker.", Value = _counters.Completed
            },
            new()
            {
                Name = "workflow_job_failures", Type = "counter",
                Help = "Workflow job attempts this worker marked as failed.", Value = _counters.Failed
            },
            new()
            {
                Name = "workflow_lease_conflicts", Type = "counter",
                Help = "Job completions rejected because the lease was no longer held.", Value = _counters.LeaseConflicts
            }
        };
        return OpenMetricsWriter.Render(samples);
    }

    private static async Task WriteJsonAsync(HttpListenerContext context, int status, string json) =>
        await WriteAsync(context, status, "application/json", json).ConfigureAwait(false);

    private static async Task WriteAsync(HttpListenerContext context, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stopping.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch
        {
        }
        _stopping.Dispose();
    }
}
