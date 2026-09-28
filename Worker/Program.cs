using Shared.Services;
using Shared.Utils;

namespace Workflow;

public static class Program
{
    public static async Task<int> Main()
    {
        var connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? "Host=postgres;Port=5432;Database=course;Username=workflow_worker;Password=worker;Include Error Detail=false";
        var owner = Environment.GetEnvironmentVariable("COURSE_WORKER_OWNER") ?? "worker-a";
        var failpoints = FailpointController.FromEnvironment();
        var testProfile = Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE") == "1";

        var leaseMs = ParseInt(
            Environment.GetEnvironmentVariable("COURSE_JOB_LEASE_MS"),
            testProfile ? 2000 : 5000);
        var pollMs = ParseInt(
            Environment.GetEnvironmentVariable("COURSE_WORKER_POLL_MS"),
            testProfile ? 100 : 1000);
        var batch = ParseInt(Environment.GetEnvironmentVariable("COURSE_CLAIM_BATCH"), 1);

        var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        var counters = new MetricsCounters();
        using var observability = new ObservabilityServer(connStr, owner, counters);
        try
        {
            observability.Start();
        }
        catch (Exception error)
        {
            StructuredLog.Write("worker.observability_failed", new Dictionary<string, object?>
            {
                ["error"] = error.GetType().Name
            });
            return 1;
        }

        var loop = new WorkerLoop(connStr, owner, failpoints, counters, leaseMs, pollMs, batch);
        StructuredLog.Write("worker.started", new Dictionary<string, object?>
        {
            ["owner"] = owner,
            ["leaseMs"] = leaseMs,
            ["pollIntervalMs"] = pollMs,
            ["failpoint"] = failpoints.Describe()
        });
        await loop.RunAsync(stopping.Token);
        return 0;
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
