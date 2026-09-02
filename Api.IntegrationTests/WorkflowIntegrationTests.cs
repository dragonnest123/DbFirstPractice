using System.Text.Json;
using System.Text.Json.Nodes;
using Cli.Services;
using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public class WorkflowIntegrationTests
{
    private readonly CourseDbFixture _db;

    public WorkflowIntegrationTests(CourseDbFixture db)
    {
        _db = db;
    }

    private FlowService Flows() => new(_db.PublicationConnection);

    // ---------- Publication ----------

    [Fact]
    public async Task Publish_NewFlow_ThenRepeatAndConflict()
    {
        var flows = Flows();
        var map = BuildMap("it-flow-1", 1);

        var published = await ResultAsync(flows.PublishAsync(map));
        Assert.Equal("published", published.RootElement.GetProperty("status").GetString());
        Assert.Equal("it-flow-1", published.RootElement.GetProperty("flowName").GetString());

        var repeated = await ResultAsync(flows.PublishAsync(map));
        Assert.Equal("exists", repeated.RootElement.GetProperty("status").GetString());

        var changed = map.Replace("\"timeout_ms\": 2000", "\"timeout_ms\": 2001", StringComparison.Ordinal);
        var ex = await Assert.ThrowsAsync<PublicationException>(() => flows.PublishAsync(changed));
        Assert.Equal("manifest.conflict", ex.Code);

        var rows = await Db.ScalarAsync(_db.SuperuserConnection,
            "SELECT count(*) FROM workflow.flow_version WHERE flow_name='it-flow-1' AND version=1");
        Assert.Equal("1", rows);
    }

    [Fact]
    public async Task Activate_SwitchesOnlyActiveFlag()
    {
        var flows = Flows();
        await flows.PublishAsync(BuildMap("it-flow-2", 1));
        await flows.PublishAsync(BuildMap("it-flow-2", 2));

        await flows.ActivateAsync("it-flow-2", 2);

        var active = await Db.ScalarAsync(_db.SuperuserConnection,
            "SELECT version FROM workflow.flow_version WHERE flow_name='it-flow-2' AND is_active");
        Assert.Equal("2", active);
    }

    [Fact]
    public async Task Activate_UnknownVersion_Fails()
    {
        var ex = await Assert.ThrowsAsync<PublicationException>(
            () => Flows().ActivateAsync("it-flow-2", 99));
        Assert.Equal("flow.not_found", ex.Code);
    }

    // ---------- Start ----------

    [Fact]
    public async Task Start_CreatesProcessWithReadyJob()
    {
        var started = await StartSeededProcessAsync();
        var state = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{started.ProcessId}'");
        Assert.Equal("RUNNING", state);
        var job = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.workflow_job WHERE process_id='{started.ProcessId}'");
        Assert.Equal("READY", job);
    }

    [Fact]
    public async Task Start_SameKeySameData_ReplaysSameProcess()
    {
        var key = "it-bk-" + Guid.NewGuid().ToString("N")[..10];
        var flows = Flows();

        var first = await ResultAsync(flows.StartAsync("workflow-smoke", key, """{"value":"x"}"""));
        var second = await ResultAsync(flows.StartAsync("workflow-smoke", key, """{"value":"x"}"""));

        Assert.Equal(
            first.RootElement.GetProperty("processId").GetString(),
            second.RootElement.GetProperty("processId").GetString());
    }

    [Fact]
    public async Task Start_SameKeyChangedData_Conflicts()
    {
        var key = "it-bk-" + Guid.NewGuid().ToString("N")[..10];
        var flows = Flows();

        await flows.StartAsync("workflow-smoke", key, """{"value":"x"}""");
        var ex = await Assert.ThrowsAsync<PublicationException>(
            () => flows.StartAsync("workflow-smoke", key, """{"value":"y"}"""));
        Assert.Equal("workflow.start_conflict", ex.Code);
    }

    [Fact]
    public async Task Start_WithoutActiveVersion_Fails()
    {
        var flows = Flows();
        await flows.PublishAsync(BuildMap("it-flow-inactive", 1));

        var ex = await Assert.ThrowsAsync<PublicationException>(
            () => flows.StartAsync("it-flow-inactive", "it-bk-x", "{}"));
        Assert.Equal("flow.inactive", ex.Code);
    }

    // ---------- Claim / finish / fail ----------

    [Fact]
    public async Task Claim_ReturnsPinnedTaskAndActionData()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);

        Assert.Equal(process.ProcessId, job["processId"]!.GetValue<string>());
        Assert.NotNull(job["executionId"]);
        Assert.Equal("invoke_canary", job["stepKey"]!.GetValue<string>());
        Assert.Equal("training", job["task"]!["module"]!.GetValue<string>());
        Assert.Equal("canary", job["task"]!["action"]!.GetValue<string>());
        Assert.Equal(1, job["task"]!["actionVersion"]!.GetValue<int>());
        Assert.Equal(3, job["task"]!["retry"]!["max_attempts"]!.GetValue<int>());
        Assert.NotNull(job["action"]!["outcomes"]);
        Assert.NotNull(job["action"]!["responseSchema"]);
        Assert.Equal("x", job["processData"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Finish_WrongOwner_LeaseStale()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);
        var jobId = job["jobId"]!.GetValue<string>();
        var leaseVersion = job["leaseVersion"]!.GetValue<long>();

        var ex = await Db.ExecErrorAsync(_db.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'other-owner',{leaseVersion},'APPLIED','{{}}'::jsonb)");
        Assert.Equal("P0001", ex.SqlState);
        Assert.Contains("workflow.lease_stale", ex.MessageText);

        var jobState = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.workflow_job WHERE job_id='{jobId}'");
        Assert.Equal("LEASED", jobState);
    }

    [Fact]
    public async Task Finish_ValidLease_AdvancesToWaitingSignal()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);
        var jobId = job["jobId"]!.GetValue<string>();
        var leaseVersion = job["leaseVersion"]!.GetValue<long>();

        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'it-owner',{leaseVersion},'APPLIED','{{\"stored\":true}}'::jsonb)");

        var jobState = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.workflow_job WHERE job_id='{jobId}'");
        Assert.Equal("SUCCEEDED", jobState);
        var processState = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{process.ProcessId}'");
        Assert.Equal("WAITING_SIGNAL", processState);
    }

    [Fact]
    public async Task Fail_Retryable_SchedulesRetryWait()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);
        var jobId = job["jobId"]!.GetValue<string>();
        var leaseVersion = job["leaseVersion"]!.GetValue<long>();

        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.fail_job('{jobId}'::uuid,'it-owner',{leaseVersion},'it.error',true)");

        var state = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.workflow_job WHERE job_id='{jobId}'");
        Assert.Equal("RETRY_WAIT", state);
        var attempt = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT attempt_number FROM workflow.task_attempt WHERE job_id='{jobId}'");
        Assert.Equal("1", attempt);
    }

    [Fact]
    public async Task Fail_ExhaustedAttempts_DeadAndTaskFailed()
    {
        var process = await StartSeededProcessAsync();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var job = await ClaimForAsync(process.ProcessId);
            Assert.Equal(attempt + 1, job["attemptNumber"]!.GetValue<int>());
            var jobId = job["jobId"]!.GetValue<string>();
            var leaseVersion = job["leaseVersion"]!.GetValue<long>();
            await Db.ScalarAsync(_db.WorkerConnection,
                $"SELECT workflow.fail_job('{jobId}'::uuid,'it-owner',{leaseVersion},'it.error',true)");
            await Task.Delay(TimeSpan.FromMilliseconds(attempt == 0 ? 300 : 500));
        }

        var jobState = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.workflow_job WHERE process_id='{process.ProcessId}'");
        Assert.Equal("DEAD", jobState);
        var processState = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{process.ProcessId}'");
        Assert.Equal("FAILED", processState);
        var attempts = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM workflow.task_attempt a JOIN workflow.workflow_job j ON j.job_id=a.job_id WHERE j.process_id='{process.ProcessId}'");
        Assert.Equal("3", attempts);
        var taskFailed = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM workflow.workflow_event WHERE process_id='{process.ProcessId}' AND event_type='TaskFailed'");
        Assert.Equal("1", taskFailed);
    }

    [Fact]
    public async Task Fail_NonRetryable_DeadImmediately()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);
        var jobId = job["jobId"]!.GetValue<string>();
        var leaseVersion = job["leaseVersion"]!.GetValue<long>();

        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.fail_job('{jobId}'::uuid,'it-owner',{leaseVersion},'it.fatal',false)");

        var jobState = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.workflow_job WHERE process_id='{process.ProcessId}'");
        Assert.Equal("DEAD", jobState);
        var attemptError = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT error_code FROM workflow.task_attempt WHERE job_id='{jobId}'");
        Assert.Equal("it.fatal", attemptError);
    }

    // ---------- Signals ----------

    [Fact]
    public async Task Signal_AcceptedDuplicateConflict()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);
        var jobId = job["jobId"]!.GetValue<string>();
        var leaseVersion = job["leaseVersion"]!.GetValue<long>();
        await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'it-owner',{leaseVersion},'APPLIED','{{}}'::jsonb)");

        var flows = Flows();
        var accepted = await ResultAsync(flows.SignalAsync(process.ProcessId, "training.completed", "it-msg-1", """{"ok":true}"""));
        Assert.Equal("accepted", accepted.RootElement.GetProperty("status").GetString());

        var duplicate = await ResultAsync(flows.SignalAsync(process.ProcessId, "training.completed", "it-msg-1", """{"ok":true}"""));
        Assert.Equal("duplicate", duplicate.RootElement.GetProperty("status").GetString());

        var ex = await Assert.ThrowsAsync<PublicationException>(
            () => flows.SignalAsync(process.ProcessId, "training.completed", "it-msg-1", """{"ok":false}"""));
        Assert.Equal("signal.conflict", ex.Code);
    }

    [Fact]
    public async Task Signal_UnknownType_Rejected()
    {
        var process = await StartSeededProcessAsync();
        var ex = await Assert.ThrowsAsync<PublicationException>(
            () => Flows().SignalAsync(process.ProcessId, "unknown.type", "it-msg-2", "{}"));
        Assert.Equal("signal.unknown", ex.Code);
    }

    // ---------- workflow.get action ----------

    [Fact]
    public async Task WorkflowGetAction_ReturnsProcessWithArrays()
    {
        var process = await StartSeededProcessAsync();
        var context = """{"principal":"it-client","requestId":"r1","correlationId":"11111111-1111-1111-1111-111111111111","scopes":["workflow:read"]}""";

        var result = await Db.ScalarAsync(_db.RuntimeConnection,
            $"SELECT api.invoke('workflow','get',NULL,'{context}'::jsonb,'{{\"processId\":\"{process.ProcessId}\"}}'::jsonb)");

        using var doc = JsonDocument.Parse(result!);
        var root = doc.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("FOUND", root.GetProperty("outcome").GetString());
        var payload = root.GetProperty("result");
        Assert.Equal(process.ProcessId, payload.GetProperty("process").GetProperty("processId").GetString());
        Assert.True(payload.GetProperty("steps").GetArrayLength() >= 1);
        Assert.True(payload.GetProperty("jobs").GetArrayLength() == 1);
        Assert.True(payload.GetProperty("attempts").GetArrayLength() == 0);
    }

    // ---------- helpers ----------

    private async Task<(string ProcessId, JsonElement Started)> StartSeededProcessAsync()
    {
        var started = await ResultAsync(Flows().StartAsync(
            "workflow-smoke", "it-bk-" + Guid.NewGuid().ToString("N")[..10], """{"value":"x"}"""));
        return (started.RootElement.GetProperty("processId").GetString()!,
                started.RootElement.Clone());
    }

    private async Task<JsonObject> ClaimForAsync(string processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var raw = await Db.ScalarAsync(_db.WorkerConnection,
                "SELECT workflow.claim_jobs('it-owner',10,2000)::text");
            using var doc = JsonDocument.Parse(raw!);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("processId", out var pid)
                    && pid.GetString() == processId)
                    return (JsonObject)JsonNode.Parse(item.GetRawText())!;
            }
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"no claimable job for process {processId}");
    }

    private static async Task<JsonDocument> ResultAsync(Task<string> task) => JsonDocument.Parse(await task);

    private static string BuildMap(string flowName, int version)
    {
        return $$"""
        {
          "contract_version": "course-1",
          "flow_name": "{{flowName}}",
          "version": {{version}},
          "start_step": "invoke",
          "steps": [
            {
              "key": "invoke",
              "type": "automatic",
              "task": {
                "service": "postgres",
                "module": "training",
                "action": "canary",
                "action_version": 1,
                "required_policy": ["workflow:execute"],
                "timeout_ms": 2000,
                "retry": {"max_attempts": 3, "delays_ms": [100, 200]},
                "input_mapping": {"/value": "/value"},
                "input_constants": {}
              }
            },
            {"key": "wait", "type": "wait_signal", "signal_type": "training.completed", "outcome": "RECEIVED"},
            {"key": "done", "type": "end", "outcome": "COMPLETED"}
          ],
          "transitions": [
            {"from": "invoke", "outcome": "APPLIED", "to": "wait"},
            {"from": "wait", "outcome": "RECEIVED", "to": "done"}
          ]
        }
        """;
    }
}