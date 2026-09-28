using System.Text.Json;
using Cli.Services;
using Npgsql;
using Workflow;
using Xunit;

namespace Api.IntegrationTests;

/// <summary>
/// Proves the Worker A stale-lease rule with the real WorkerLoop: when the lease
/// moves on while the action is in flight, the losing replica writes nothing.
/// Not even a failure marker may survive, so the new lease holder keeps ownership.
/// </summary>
[Collection("worker-loop")]
public sealed class WorkerStaleLeaseTests
{
    private const string ValidRequestSchema =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["value"],"properties":{"value":{"type":"string","minLength":1,"maxLength":128}}}""";

    private const string ValidResponseSchema =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["stored"],"properties":{"stored":{"type":"boolean"}}}""";

    private const int LeaseMs = 2000;

    private readonly WorkerLoopFixture _db;

    public WorkerStaleLeaseTests(WorkerLoopFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task StaleLeaseDuringAction_WritesNothing_AndLeavesTheNewOwnerInCharge()
    {
        var actionName = "canary_lease_" + Guid.NewGuid().ToString("N")[..8];
        var flowName = "it-lease-" + Guid.NewGuid().ToString("N")[..8];
        await InsertCatalogActionAsync(actionName);
        var processId = await StartWithActionAsync(flowName, actionName);
        var jobId = await AwaitClaimedJobAsync(processId);

        // Freeze the worker between the action effect and the completion by
        // holding an exclusive lock on the effect table.
        await using var blocker = new NpgsqlConnection(_db.SuperuserConnection);
        await blocker.OpenAsync();
        await using var blockerTx = await blocker.BeginTransactionAsync();
        await using (var lockCmd = new NpgsqlCommand(
            "LOCK TABLE training.canary_effects IN ACCESS EXCLUSIVE MODE", blocker, blockerTx))
        {
            await lockCmd.ExecuteNonQueryAsync();
        }

        var counters = new MetricsCounters();
        using var cts = new CancellationTokenSource();
        var loop = new WorkerLoop(
            _db.WorkerConnection,
            "worker-a",
            new Shared.Services.FailpointController(null, false),
            counters,
            LeaseMs,
            50,
            1);
        var runTask = loop.RunAsync(cts.Token);
        try
        {
            await AwaitOwnerAsync(jobId, "worker-a");

            // The lease expires while worker-a is still blocked, worker-b takes over.
            var stolen = await StealAfterLeaseExpiryAsync(jobId);
            Assert.Equal(2, stolen);

            await blockerTx.CommitAsync();

            await WaitForAsync(async () => counters.LeaseConflicts == 1, "lease conflict was not recorded");

            Assert.Equal("LEASED",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT state FROM autocheck.jobs WHERE job_id='{jobId}'"));
            Assert.Equal("worker-b",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT lease_owner FROM autocheck.jobs WHERE job_id='{jobId}'"));
            Assert.Equal("2",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT lease_version FROM autocheck.jobs WHERE job_id='{jobId}'"));

            // The fenced replica committed nothing: no effect, no dispatch, no failure.
            Assert.Equal("0",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    "SELECT count(*) FROM training.canary_effects ce JOIN workflow.workflow_job j ON j.execution_id::text=ce.execution_id " +
                    $"WHERE j.process_id='{processId}'"));
            Assert.Equal("STALE,RUNNING",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT string_agg(status, ',' ORDER BY attempt_number) FROM autocheck.attempts WHERE job_id='{jobId}'"));
            Assert.Equal("0",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT count(*) FROM autocheck.attempts WHERE job_id='{jobId}' AND status='FAILED'"));

            // The surviving owner still completes the same job.
            await Db.ScalarAsync(_db.WorkerConnection,
                $"SELECT workflow.finish_job('{jobId}'::uuid,'worker-b',{stolen},'APPLIED','{{\"stored\":true}}'::jsonb)");
            Assert.Equal("SUCCEEDED",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT state FROM autocheck.jobs WHERE job_id='{jobId}'"));
            Assert.Equal("STALE,SUCCEEDED",
                await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT string_agg(status, ',' ORDER BY attempt_number) FROM autocheck.attempts WHERE job_id='{jobId}'"));
            Assert.Equal(0, counters.Failed);
            Assert.Equal(0, counters.Completed);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task<string> AwaitClaimedJobAsync(string processId)
    {
        await WaitForAsync(async () =>
            (await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT count(*) FROM autocheck.jobs WHERE process_id='{processId}'")) == "1",
            $"no job was created for process {processId}");

        return (await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT job_id FROM autocheck.jobs WHERE process_id='{processId}'"))!;
    }

    private async Task AwaitOwnerAsync(string jobId, string owner)
    {
        await WaitForAsync(async () =>
            await Db.ScalarAsync(_db.SuperuserConnection,
                $"SELECT lease_owner FROM autocheck.jobs WHERE job_id='{jobId}'") == owner,
            $"worker did not claim job {jobId}");
    }

    private async Task<long> StealAfterLeaseExpiryAsync(string jobId)
    {
        await WaitForAsync(async () =>
        {
            var raw = await Db.ScalarAsync(_db.WorkerConnection,
                $"SELECT workflow.claim_jobs('worker-b',100,{LeaseMs})::text");
            using var document = JsonDocument.Parse(raw!);
            return document.RootElement.EnumerateArray().Any(item =>
                item.GetProperty("jobId").GetString() == jobId);
        }, $"worker-b could not steal job {jobId}");

        return long.Parse((await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT lease_version FROM autocheck.jobs WHERE job_id='{jobId}'"))!);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, string message)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException(message);
    }

    private async Task InsertCatalogActionAsync(string actionName)
    {
        await Db.ScalarAsync(_db.SuperuserConnection,
            $$"""
            INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
            VALUES ('training','{{actionName}}',1,'POST','training','canary_v1',
              '{{ValidRequestSchema}}'::jsonb,
              '{{ValidResponseSchema}}'::jsonb,
              '["APPLIED"]'::jsonb,
              '["workflow:execute"]'::jsonb,
              'required','principal_action',5000,true,true,'course-1')
            """);
    }

    private async Task<string> StartWithActionAsync(string flowName, string actionName)
    {
        var flows = new FlowService(_db.PublicationConnection);
        var map = $$"""
        {
          "contract_version": "course-1",
          "flow_name": "{{flowName}}",
          "version": 1,
          "start_step": "invoke",
          "steps": [
            {
              "key": "invoke",
              "type": "automatic",
              "task": {
                "service": "postgres",
                "module": "training",
                "action": "{{actionName}}",
                "action_version": 1,
                "required_policy": ["workflow:execute"],
                "timeout_ms": 5000,
                "retry": {"max_attempts": 1, "delays_ms": []},
                "input_mapping": {"/value": "/value"},
                "input_constants": {}
              }
            },
            {"key": "done", "type": "end", "outcome": "FINISHED"}
          ],
          "transitions": [
            {"from": "invoke", "outcome": "APPLIED", "to": "done"}
          ]
        }
        """;
        var published = JsonDocument.Parse(await flows.PublishAsync(map)).RootElement;
        Assert.Equal("published", published.GetProperty("status").GetString());
        await flows.ActivateAsync(flowName, 1);

        var started = JsonDocument.Parse(
            await flows.StartAsync(flowName, "it-ls-" + Guid.NewGuid().ToString("N")[..10], """{"value":"x"}""")).RootElement;
        return started.GetProperty("processId").GetString()!;
    }
}
