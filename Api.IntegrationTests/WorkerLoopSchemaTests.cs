using System.Text.Json;
using Cli.Services;
using Workflow;
using Xunit;

namespace Api.IntegrationTests;

/// <summary>
/// Runs the real WorkerLoop against an isolated database to prove fail-closed
/// schema handling: missing/malformed schemas must produce a contract error
/// with rolled back target effect and no finish.
/// </summary>
[Collection("worker-loop")]
public sealed class WorkerLoopSchemaTests
{
    private const string ValidRequestSchema =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["value"],"properties":{"value":{"type":"string","minLength":1,"maxLength":128}}}""";

    private const string ValidResponseSchema =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","additionalProperties":false,"required":["stored","echo"],"properties":{"stored":{"type":"boolean"},"echo":{"type":"string"}}}""";

    private readonly WorkerLoopFixture _db;

    public WorkerLoopSchemaTests(WorkerLoopFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task MalformedResponseSchema_FailsWithContractError_NoEffectNoFinish()
    {
        await InsertCatalogActionAsync("canary_broken_resp", ValidRequestSchema,
            """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":123}""");
        var processId = await StartWithActionAsync("it-resp-flow", "canary_broken_resp",
            """{"/value": "/value"}""", "{}");

        var (jobState, errorCode) = await RunWorkerAndWaitForDeadAsync(processId);

        Assert.Equal("DEAD", jobState);
        Assert.Equal("workflow.contract_invalid", errorCode);
        Assert.Equal("0", await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM training.canary_effects ce JOIN workflow.workflow_job j ON j.execution_id::text=ce.execution_id WHERE j.process_id='{processId}'"));
        Assert.Equal("FAILED", await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.step_instance WHERE process_id='{processId}'"));
        Assert.Equal("FAILED", await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{processId}'"));
    }

    [Fact]
    public async Task RequestMismatch_FailsWithPayloadError_NoEffectNoFinish()
    {
        await InsertCatalogActionAsync("canary_mismatch_req", ValidRequestSchema, ValidResponseSchema);
        var processId = await StartWithActionAsync("it-req-flow", "canary_mismatch_req",
            "{}", """{"value": 123}""");

        var (jobState, errorCode) = await RunWorkerAndWaitForDeadAsync(processId);

        Assert.Equal("DEAD", jobState);
        Assert.Equal("workflow.payload_invalid", errorCode);
        Assert.Equal("0", await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM training.canary_effects ce JOIN workflow.workflow_job j ON j.execution_id::text=ce.execution_id WHERE j.process_id='{processId}'"));
        Assert.Equal("FAILED", await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.step_instance WHERE process_id='{processId}'"));
        Assert.Equal("FAILED", await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{processId}'"));
    }

    private async Task InsertCatalogActionAsync(string actionName, string requestSchema, string responseSchema)
    {
        await Db.ScalarAsync(_db.SuperuserConnection,
            $$"""
            INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
            VALUES ('training','{{actionName}}',1,'POST','training','canary_v1',
              '{{requestSchema}}'::jsonb,
              '{{responseSchema}}'::jsonb,
              '["APPLIED"]'::jsonb,
              '["workflow:execute"]'::jsonb,
              'required','principal_action',2000,true,true,'course-1')
            """);
    }

    private async Task<string> StartWithActionAsync(string flowName, string actionName, string inputMapping, string inputConstants)
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
                "timeout_ms": 2000,
                "retry": {"max_attempts": 1, "delays_ms": []},
                "input_mapping": {{inputMapping}},
                "input_constants": {{inputConstants}}
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
            await flows.StartAsync(flowName, "it-bk-" + Guid.NewGuid().ToString("N")[..10], """{"value":"x"}""")).RootElement;
        return started.GetProperty("processId").GetString()!;
    }

    private async Task<(string JobState, string ErrorCode)> RunWorkerAndWaitForDeadAsync(string processId)
    {
        using var cts = new CancellationTokenSource();
        var loop = new WorkerLoop(_db.WorkerConnection, "it-worker", "", 2000, 50, 1);
        var runTask = loop.RunAsync(cts.Token);
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var row = await Db.ScalarAsync(_db.SuperuserConnection,
                    $"SELECT j.state||'|'||COALESCE(a.error_code,'') FROM workflow.workflow_job j " +
                    "LEFT JOIN workflow.task_attempt a ON a.job_id=j.job_id AND a.status='FAILED' " +
                    $"WHERE j.process_id='{processId}'");
                if (row is not null && row.StartsWith("DEAD|", StringComparison.Ordinal))
                    return ("DEAD", row["DEAD|".Length..]);
                await Task.Delay(100);
            }
            throw new Xunit.Sdk.XunitException($"worker did not finish the job for process {processId}");
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
}