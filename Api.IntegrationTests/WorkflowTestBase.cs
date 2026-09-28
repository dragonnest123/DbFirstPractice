using System.Text.Json;
using System.Text.Json.Nodes;
using Cli.Services;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public abstract class WorkflowTestBase
{
    protected readonly CourseDbFixture _db;

    protected WorkflowTestBase(CourseDbFixture db)
    {
        _db = db;
    }

    protected FlowService Flows() => new(_db.PublicationConnection);

    protected async Task<(string ProcessId, JsonElement Started)> StartSeededProcessAsync()
    {
        var started = await ResultAsync(Flows().StartAsync(
            "workflow-smoke", "it-bk-" + Guid.NewGuid().ToString("N")[..10], """{"value":"x"}"""));
        return (started.RootElement.GetProperty("processId").GetString()!,
                started.RootElement.Clone());
    }

    protected async Task<JsonObject> ClaimForAsync(string processId)
    {
        return await ClaimForOwnerAsync("it-owner", processId);
    }

    protected async Task<JsonObject> ClaimForOwnerAsync(string owner, string processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var job = await ClaimOnceAsync(owner, processId);
            if (job is not null)
                return job;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"no claimable job for process {processId} by {owner}");
    }

    protected async Task<JsonObject?> ClaimOnceAsync(string owner, string? processId = null)
    {
        var raw = await Db.ScalarAsync(_db.WorkerConnection,
            $"SELECT workflow.claim_jobs('{owner}',100,2000)::text");
        using var doc = JsonDocument.Parse(raw!);
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var pid = item.GetProperty("processId").GetString();
            if (processId is null || pid == processId)
                return (JsonObject)JsonNode.Parse(item.GetRawText())!;
        }
        return null;
    }

    protected static async Task<JsonDocument> ResultAsync(Task<string> task) => JsonDocument.Parse(await task);

    protected static string BuildMap(string flowName, int version)
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