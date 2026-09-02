using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Npgsql;

namespace Workflow;

public sealed class WorkerLoop
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _connStr;
    private readonly string _owner;
    private readonly string _failpoint;
    private readonly int _leaseMs;
    private readonly int _pollIntervalMs;
    private readonly int _batch;
    private readonly int _attemptTimeoutMs;

    public WorkerLoop(string connStr, string owner, string failpoint, int leaseMs, int pollIntervalMs, int batch)
    {
        _connStr = connStr;
        _owner = owner;
        _failpoint = failpoint;
        _leaseMs = leaseMs;
        _pollIntervalMs = pollIntervalMs;
        _batch = batch;
        _attemptTimeoutMs = 30000;
    }

    public async Task RunAsync(CancellationToken stopping)
    {
        Log("worker.started", new { owner = _owner, leaseMs = _leaseMs, pollIntervalMs = _pollIntervalMs });

        while (!stopping.IsCancellationRequested)
        {
            List<JsonObject>? claimed = null;
            try
            {
                claimed = await ClaimAsync(stopping);
            }
            catch (Exception ex)
            {
                Log("worker.claim_failed", new { error = ex.Message });
            }

            if (claimed is null || claimed.Count == 0)
            {
                try
                {
                    await Task.Delay(_pollIntervalMs, stopping);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            foreach (var job in claimed)
            {
                try
                {
                    await ProcessJobAsync(job, stopping);
                }
                catch (Exception ex)
                {
                    Log("worker.job_failed", new { jobId = Job(job, "jobId"), error = ex.Message });
                }
            }
        }

        Log("worker.stopped", new { owner = _owner });
    }

    private async Task<List<JsonObject>> ClaimAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connStr);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT workflow.claim_jobs(@o,@b,@l)::text", conn);
        cmd.Parameters.AddWithValue("o", _owner);
        cmd.Parameters.AddWithValue("b", _batch);
        cmd.Parameters.AddWithValue("l", _leaseMs);

        var raw = (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "[]";
        var node = JsonNode.Parse(raw) as JsonArray ?? [];
        return node.OfType<JsonObject>().ToList();
    }

    private async Task ProcessJobAsync(JsonObject job, CancellationToken stopping)
    {
        var jobId = Job(job, "jobId");
        var executionId = Job(job, "executionId");
        var attemptId = Job(job, "attemptId");
        var processId = Job(job, "processId");

        Log("job.claimed", new { jobId, executionId, attemptId, owner = _owner });

        if (_failpoint == "after_job_claim")
        {
            LogFailpoint("after_job_claim");
            BlockForever();
        }

        var task = job["task"] as JsonObject ?? new JsonObject();
        var action = job["action"] as JsonObject ?? new JsonObject();
        var module = Text(task, "module") ?? "";
        var actionName = Text(task, "action") ?? "";
        var actionVersion = Int(task, "actionVersion");
        var timeoutMs = Int(task, "timeoutMs");
        var retryableTimeout = timeoutMs > 0 ? Math.Min(timeoutMs, _attemptTimeoutMs) : _attemptTimeoutMs;

        if (module.Length == 0 || actionName.Length == 0 || actionVersion < 1)
        {
            await FailJobAsync(job, "workflow.task_invalid", false);
            return;
        }

        var payload = BuildPayload(job, out var payloadError);
        if (payloadError is not null)
        {
            await FailJobAsync(job, "workflow.mapping_missing", false);
            return;
        }

        var requestSchema = LoadSchema(action["requestSchema"]);
        if (requestSchema is not null && !requestSchema.Evaluate(payload).IsValid)
        {
            await FailJobAsync(job, "workflow.payload_invalid", false);
            return;
        }

        using var timeoutCts = new CancellationTokenSource(retryableTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, stopping);

        NpgsqlConnection? conn = null;
        NpgsqlTransaction? tx = null;
        try
        {
            conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync(linkedCts.Token);
            tx = await conn.BeginTransactionAsync(linkedCts.Token);

            var context = new JsonObject
            {
                ["principal"] = "workflow-worker",
                ["requestId"] = executionId,
                ["correlationId"] = executionId,
                ["processId"] = processId,
                ["jobId"] = jobId,
                ["executionId"] = executionId,
                ["attemptId"] = attemptId,
                ["deadline"] = DateTimeOffset.UtcNow.AddMilliseconds(retryableTimeout)
                    .ToString("O"),
                ["recordDispatch"] = true
            };
            if (task["requiredPolicy"] is JsonArray scopes)
                context["scopes"] = scopes.DeepClone();

            var invokeJson = await InvokeAsync(
                conn, tx, module, actionName, actionVersion, context, payload, linkedCts.Token);

            JsonObject envelope;
            try
            {
                envelope = (JsonNode.Parse(invokeJson) as JsonObject)!;
            }
            catch
            {
                await RollbackAsync(tx, linkedCts.Token);
                await FailJobAsync(job, "workflow.contract_invalid", false);
                return;
            }

            if (Text(envelope, "status") == "error")
            {
                var retryable = envelope.TryGetPropertyValue("retryable", out var r)
                    && r?.GetValue<bool>() == true;
                await RollbackAsync(tx, linkedCts.Token);
                await FailJobAsync(job, Text(envelope, "code") ?? "workflow.unknown_error", retryable);
                return;
            }

            var outcome = Text(envelope, "outcome");
            if (outcome is null || !OutcomeAllowed(action, outcome))
            {
                await RollbackAsync(tx, linkedCts.Token);
                await FailJobAsync(job, "workflow.unknown_outcome", false);
                return;
            }

            var result = envelope["result"];
            var responseSchema = LoadSchema(action["responseSchema"]);
            if (result is null || (responseSchema is not null && !responseSchema.Evaluate(result).IsValid))
            {
                await RollbackAsync(tx, linkedCts.Token);
                await FailJobAsync(job, "workflow.response_invalid", false);
                return;
            }

            if (_failpoint == "after_action_before_finish")
            {
                LogFailpoint("after_action_before_finish");
                BlockForever();
            }

            await FinishJobAsync(conn, tx, job, outcome, result.ToJsonString(), linkedCts.Token);
            await tx.CommitAsync(linkedCts.Token);

            Log("job.finished", new { jobId, executionId, outcome, owner = _owner });
        }
        catch (OperationCanceledException)
        {
            await RollbackAsync(tx);
            Log("job.timeout", new { jobId, owner = _owner });
            await FailJobAsync(job, "workflow.timeout", true);
        }
        catch (NpgsqlException ex)
        {
            await RollbackAsync(tx);
            Log("job.db_failed", new { jobId, error = ex.Message });
        }
        finally
        {
            if (tx is not null)
                await tx.DisposeAsync();
            if (conn is not null)
                await conn.DisposeAsync();
        }
    }

    private JsonObject BuildPayload(JsonObject job, out string? error)
    {
        error = null;
        var task = job["task"] as JsonObject ?? new JsonObject();
        var processData = job["processData"];
        var constants = task["inputConstants"]?.DeepClone() as JsonObject ?? new JsonObject();

        if (task["inputMapping"] is JsonObject mapping)
        {
            foreach (var property in mapping)
            {
                var sourcePointer = property.Value?.GetValue<string>();
                if (sourcePointer is null)
                {
                    error = "invalid mapping source";
                    return constants;
                }
                var sourceValue = processData is null ? null : JsonPointer.Resolve(processData, sourcePointer);
                if (sourceValue is null)
                {
                    error = "missing process data";
                    return constants;
                }
                if (!JsonPointer.Set(constants, property.Key, sourceValue))
                {
                    error = "invalid mapping target";
                    return constants;
                }
            }
        }
        return constants;
    }

    private static bool OutcomeAllowed(JsonObject action, string outcome)
    {
        if (action["outcomes"] is not JsonArray outcomes)
            return false;
        return outcomes.Any(item => item?.GetValue<string>() == outcome);
    }

    private static JsonSchema? LoadSchema(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return JsonSchema.FromText(node.ToJsonString());
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> InvokeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx,
        string module, string action, int actionVersion,
        JsonObject context, JsonObject payload, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT api.invoke(@m,@a,@v,@ctx::jsonb,@pay::jsonb)::text", conn, tx);
        cmd.CommandTimeout = Math.Max(1, 30 + 2);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("v", actionVersion);
        cmd.Parameters.AddWithValue("ctx", context.ToJsonString());
        cmd.Parameters.AddWithValue("pay", payload.ToJsonString());

        return (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "";
    }

    private static async Task FinishJobAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, JsonObject job,
        string outcome, string resultJson, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT workflow.finish_job(@j::uuid,@o,@lv,@oc,@r::jsonb)::text", conn, tx);
        cmd.Parameters.AddWithValue("j", Job(job, "jobId")!);
        cmd.Parameters.AddWithValue("o", Job(job, "owner")!);
        cmd.Parameters.AddWithValue("lv", long.Parse(Job(job, "leaseVersion")!));
        cmd.Parameters.AddWithValue("oc", outcome);
        cmd.Parameters.AddWithValue("r", resultJson);
        await cmd.ExecuteScalarAsync(ct);
    }

    private async Task FailJobAsync(JsonObject job, string errorCode, bool retryable)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connStr);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                "SELECT workflow.fail_job(@j::uuid,@o,@lv,@c,@r)::text", conn);
            cmd.Parameters.AddWithValue("j", Job(job, "jobId")!);
            cmd.Parameters.AddWithValue("o", Job(job, "owner")!);
            cmd.Parameters.AddWithValue("lv", long.Parse(Job(job, "leaseVersion")!));
            cmd.Parameters.AddWithValue("c", errorCode);
            cmd.Parameters.AddWithValue("r", retryable);

            await cmd.ExecuteScalarAsync();
            Log("job.failed", new { jobId = Job(job, "jobId"), errorCode, retryable, owner = _owner });
        }
        catch (Exception ex)
        {
            Log("job.fail_failed", new { jobId = Job(job, "jobId"), error = ex.Message });
        }
    }

    private static async Task RollbackAsync(NpgsqlTransaction? tx, CancellationToken ct = default)
    {
        if (tx is null)
            return;
        try
        {
            await tx.RollbackAsync(ct);
        }
        catch
        {
        }
    }

    private static void BlockForever()
    {
        while (true)
            Thread.Sleep(TimeSpan.FromHours(1));
    }

    private static string? Job(JsonObject job, string key) =>
        job.TryGetPropertyValue(key, out var value) switch
        {
            true when value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text) => text,
            true when value is JsonValue jsonValue && jsonValue.TryGetValue<long>(out var number) => number.ToString(),
            true when value is not null => value.ToString(),
            _ => null
        };

    private static string? Text(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var value) ? value?.GetValue<string>() : null;

    private static int Int(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var value) && value is not null ? value.GetValue<int>() : 0;

    private void LogFailpoint(string name)
    {
        Console.Out.WriteLine(
            $"{{\"event\":\"failpoint.reached\",\"name\":\"{name}\",\"instanceId\":\"{_owner}\"}}");
        Console.Out.Flush();
    }

    private static void Log(string eventName, object details)
    {
        var payload = new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["event"] = eventName
        };
        if (details is not null)
            payload["details"] = JsonNode.Parse(JsonSerializer.Serialize(details, JsonOptions));

        Console.Out.WriteLine(payload.ToJsonString());
        Console.Out.Flush();
    }
}