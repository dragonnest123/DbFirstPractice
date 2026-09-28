using System.Text.Json;
using System.Text.Json.Nodes;
using Cli.Services;
using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

/// <summary>DB regression suite: claim fencing, stale finish, crash recovery and signal retry.</summary>
[Collection("course-db")]
public sealed class WorkflowFencingTests : WorkflowTestBase
{
    public WorkflowFencingTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task CompetingClaims_SecondOwnerBlockedUntilLeaseExpiry()
    {
        var process = await StartSeededProcessAsync();
        var first = await ClaimOnceAsync("worker-a", process.ProcessId);
        Assert.NotNull(first);
        var jobId = first!["jobId"]!.GetValue<string>();
        var executionId = first!["executionId"]!.GetValue<string>();
        var firstAttemptId = first!["attemptId"]!.GetValue<string>();

        var blocked = await ClaimOnceAsync("worker-b", process.ProcessId);
        Assert.Null(blocked);

        Assert.Equal("LEASED|worker-a",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT state||'|'||lease_owner FROM autocheck.jobs WHERE job_id='{jobId}'"));
        Assert.Equal("1",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT count(*) FROM autocheck.attempts WHERE job_id='{jobId}'"));

        await Task.Delay(2500);

        var reclaimed = await ClaimOnceAsync("worker-b", process.ProcessId);
        Assert.NotNull(reclaimed);
        Assert.Equal(jobId, reclaimed!["jobId"]!.GetValue<string>());
        Assert.Equal(executionId, reclaimed!["executionId"]!.GetValue<string>());
        Assert.NotEqual(firstAttemptId, reclaimed!["attemptId"]!.GetValue<string>());
        Assert.Equal(2, reclaimed!["attemptNumber"]!.GetValue<int>());
        Assert.Equal("2",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT lease_version FROM autocheck.jobs WHERE job_id='{jobId}'"));
        Assert.Equal("STALE,RUNNING",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT string_agg(status, ',' ORDER BY attempt_number) FROM autocheck.attempts WHERE job_id='{jobId}'"));
    }

    [Fact]
    public async Task StaleFinish_AfterReclaim_IsRejectedAndLeaseKept()
    {
        var process = await StartSeededProcessAsync();
        var first = await ClaimOnceAsync("it-owner", process.ProcessId);
        var jobId = first!["jobId"]!.GetValue<string>();
        var staleVersion = first!["leaseVersion"]!.GetValue<long>();

        await Task.Delay(2500);

        var second = await ClaimOnceAsync("it-owner", process.ProcessId);
        var currentVersion = second!["leaseVersion"]!.GetValue<long>();
        Assert.Equal(staleVersion + 1, currentVersion);

        var ex = await Db.ExecErrorAsync(_db.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'it-owner',{staleVersion},'APPLIED','{{}}'::jsonb)");
        Assert.Equal("P0001", ex.SqlState);
        Assert.Contains("workflow.lease_stale", ex.MessageText);

        Assert.Equal("LEASED",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT state FROM autocheck.jobs WHERE job_id='{jobId}'"));
        Assert.Equal("STALE,RUNNING",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT string_agg(status, ',' ORDER BY attempt_number) FROM autocheck.attempts WHERE job_id='{jobId}'"));

        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'it-owner',{currentVersion},'APPLIED','{{\"stored\":true}}'::jsonb)");
        Assert.Equal("SUCCEEDED",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT state FROM autocheck.jobs WHERE job_id='{jobId}'"));
        Assert.Equal("WAITING_SIGNAL",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT state FROM autocheck.processes WHERE process_id='{process.ProcessId}'"));
    }

    [Fact]
    public async Task CrashBetweenActionAndFinish_RollbackPreventsDuplicateEffect()
    {
        var process = await StartSeededProcessAsync();
        var first = await ClaimOnceAsync("it-owner", process.ProcessId);
        var jobId = first!["jobId"]!.GetValue<string>();
        var executionId = first!["executionId"]!.GetValue<string>();
        var firstAttemptId = first!["attemptId"]!.GetValue<string>();

        var invoked = await InvokeCanaryAsync(first!, commit: false);
        Assert.Equal("ok", invoked.GetProperty("status").GetString());
        Assert.Equal("0",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT count(*) FROM training.canary_effects WHERE execution_id='{executionId}'"));

        await Task.Delay(2500);

        var second = await ClaimOnceAsync("it-owner", process.ProcessId);
        Assert.NotNull(second);
        Assert.Equal(jobId, second!["jobId"]!.GetValue<string>());
        Assert.Equal(executionId, second!["executionId"]!.GetValue<string>());
        Assert.NotEqual(firstAttemptId, second!["attemptId"]!.GetValue<string>());

        var redone = await InvokeCanaryAsync(second!, commit: true);
        Assert.Equal("ok", redone.GetProperty("status").GetString());
        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'it-owner',{second!["leaseVersion"]!.GetValue<long>()},'APPLIED','{{\"stored\":true}}'::jsonb)");

        Assert.Equal("1",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT count(*) FROM training.canary_effects WHERE execution_id='{executionId}'"));
        Assert.Equal("STALE,SUCCEEDED",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT string_agg(status, ',' ORDER BY attempt_number) FROM autocheck.attempts WHERE job_id='{jobId}'"));
        Assert.Equal("SUCCEEDED",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT state FROM autocheck.jobs WHERE job_id='{jobId}'"));
    }

    [Fact]
    public async Task RetryThenSignal_ConflictAndDuplicate_PreservesHistory()
    {
        var process = await StartSeededProcessAsync();
        var first = await ClaimOnceAsync("it-owner", process.ProcessId);
        var jobId = first!["jobId"]!.GetValue<string>();
        var executionId = first!["executionId"]!.GetValue<string>();
        var firstAttemptId = first!["attemptId"]!.GetValue<string>();

        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.fail_job('{jobId}'::uuid,'it-owner',{first!["leaseVersion"]!.GetValue<long>()},'it.transient',true)");

        var second = await ClaimForOwnerAsync("it-owner", process.ProcessId);
        Assert.Equal(jobId, second["jobId"]!.GetValue<string>());
        Assert.Equal(executionId, second["executionId"]!.GetValue<string>());
        Assert.NotEqual(firstAttemptId, second["attemptId"]!.GetValue<string>());
        Assert.Equal(2, second["attemptNumber"]!.GetValue<int>());

        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'it-owner',{second["leaseVersion"]!.GetValue<long>()},'APPLIED','{{\"stored\":true}}'::jsonb)");

        var flows = Flows();
        var accepted = await ResultAsync(
            flows.SignalAsync(process.ProcessId, "training.completed", "it-fence-msg-1", """{"ok":true}"""));
        Assert.Equal("accepted", accepted.RootElement.GetProperty("status").GetString());
        var duplicate = await ResultAsync(
            flows.SignalAsync(process.ProcessId, "training.completed", "it-fence-msg-1", """{"ok":true}"""));
        Assert.Equal("duplicate", duplicate.RootElement.GetProperty("status").GetString());
        var conflict = await Assert.ThrowsAsync<PublicationException>(
            () => flows.SignalAsync(process.ProcessId, "training.completed", "it-fence-msg-1", """{"ok":false}"""));
        Assert.Equal("signal.conflict", conflict.Code);

        Assert.Equal("1",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT count(*) FROM autocheck.signals WHERE process_id='{process.ProcessId}'"));
        Assert.Equal("FAILED,SUCCEEDED",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT string_agg(status, ',' ORDER BY attempt_number) FROM autocheck.attempts WHERE job_id='{jobId}'"));
        Assert.Equal("SUCCEEDED",
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT state FROM autocheck.jobs WHERE job_id='{jobId}'"));
    }

    private async Task<JsonElement> InvokeCanaryAsync(JsonObject job, bool commit)
    {
        await using var conn = new NpgsqlConnection(_db.WorkerConnection);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT api.invoke(@m,@a,@v,@ctx::jsonb,@pay::jsonb)::text", conn, tx);
        cmd.Parameters.AddWithValue("m", "training");
        cmd.Parameters.AddWithValue("a", "canary");
        cmd.Parameters.AddWithValue("v", 1);
        cmd.Parameters.AddWithValue("ctx", WorkerContext(job).ToJsonString());
        cmd.Parameters.AddWithValue("pay", """{"value":"fence-test"}""");
        var raw = (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
        if (commit)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return JsonDocument.Parse(raw).RootElement;
    }

    private static JsonObject WorkerContext(JsonObject job)
    {
        var processId = job["processId"]!.GetValue<string>();
        var jobId = job["jobId"]!.GetValue<string>();
        var executionId = job["executionId"]!.GetValue<string>();
        var attemptId = job["attemptId"]!.GetValue<string>();
        return new JsonObject
        {
            ["principal"] = "workflow-worker",
            ["requestId"] = executionId,
            ["correlationId"] = executionId,
            ["processId"] = processId,
            ["jobId"] = jobId,
            ["executionId"] = executionId,
            ["attemptId"] = attemptId,
            ["deadline"] = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            ["recordDispatch"] = true,
            ["scopes"] = new JsonArray("workflow:execute")
        };
    }
}