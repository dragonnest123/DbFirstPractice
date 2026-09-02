namespace Workflow;

public static class Program
{
    public static async Task<int> Main()
    {
        var connStr = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
            ?? "Host=postgres;Port=5432;Database=course;Username=workflow_worker;Password=worker;Include Error Detail=false";
        var owner = Environment.GetEnvironmentVariable("COURSE_WORKER_OWNER") ?? "worker-a";
        var failpoint = Environment.GetEnvironmentVariable("COURSE_FAILPOINT") ?? "";
        var testProfile = Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE") == "1";

        var leaseMs = ParseInt(Environment.GetEnvironmentVariable("COURSE_LEASE_MS"), testProfile ? 2000 : 5000);
        var pollMs = ParseInt(Environment.GetEnvironmentVariable("COURSE_POLL_INTERVAL_MS"), testProfile ? 100 : 1000);
        var batch = ParseInt(Environment.GetEnvironmentVariable("COURSE_CLAIM_BATCH"), 1);

        var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        var loop = new WorkerLoop(connStr, owner, failpoint, leaseMs, pollMs, batch);
        await loop.RunAsync(stopping.Token);
        return 0;
    }

    private static int ParseInt(string? value, int fallback)
    {
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}