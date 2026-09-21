using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

/// <summary>Shared helpers for the week-3 (payment perimeter) integration tests.</summary>
[Collection("course-db")]
public abstract class PaymentPerimeterTestBase
{
    protected readonly CourseDbFixture Fixture;

    protected PaymentPerimeterTestBase(CourseDbFixture db)
    {
        Fixture = db;
    }

    protected async Task<string> CreateOperationAsync(string kind, string amount)
    {
        await using var conn = new NpgsqlConnection(Fixture.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            INSERT INTO payment.operations(operation_id, request_id, principal, operation_kind, amount, currency, status)
            VALUES (gen_random_uuid(), 'it-req-' || gen_random_uuid()::text, 'it-principal', @kind, @amount::numeric, 'RUB', 'CREATED')
            RETURNING operation_id
            """, conn);
        cmd.Parameters.AddWithValue("kind", kind);
        cmd.Parameters.AddWithValue("amount", amount);
        return (await cmd.ExecuteScalarAsync())?.ToString()
            ?? throw new InvalidOperationException("no operation created");
    }

    protected async Task<JsonElement> SubmitAsync(string operationId, string requestId)
    {
        var result = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "payment", "submit", 1,
                $$"""{"principal":"it-principal","requestId":"{{requestId}}","correlationId":"11111111-1111-1111-1111-111111111111","scopes":["payment:write"]}""",
                $$"""{"operationId":"{{operationId}}"}""")).RootElement;
        if (result.GetProperty("status").GetString() != "ok")
            throw new Xunit.Sdk.XunitException($"submit failed: {result.GetRawText()}");
        return result.Clone();
    }

    protected async Task<(string OperationId, string ProcessId)> CreateSubmittedAsync(string kind, string amount, string requestId)
    {
        var operationId = await CreateOperationAsync(kind, amount);
        var submit = await SubmitAsync(operationId, requestId);
        var processId = submit.GetProperty("result").GetProperty("processId").GetString()!;
        return (operationId, processId);
    }

    protected async Task<string> ExternalIdAsync(string operationId)
    {
        return (await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT external_request_id FROM delivery.external_request WHERE operation_id='{operationId}'"))!;
    }

    protected async Task PrepareExternalDirectAsync(string operationId)
    {
        await InvokeAsync(Fixture.SuperuserConnection, "payment", "prepare_external", 1,
            """{"principal":"workflow-worker","requestId":"11111111-1111-1111-1111-111111111111","correlationId":"11111111-1111-1111-1111-111111111111","scopes":["payment:internal"]}""",
            $$"""{"operationId":"{{operationId}}"}""");
    }

    protected async Task PrepareAsync(string requestId)
    {
        var (operationId, _) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", requestId);
        await PrepareExternalDirectAsync(operationId);
    }

    protected static string ReceiptBody(string externalId, string messageId, string outcome)
    {
        return $$"""{"externalRequestId":"{{externalId}}","messageId":"{{messageId}}","occurredAt":"2026-09-04T12:00:00Z","outcome":"{{outcome}}","providerPaymentId":"{{messageId}}","version":1}""";
    }

    protected async Task AcceptReceiptAsync(string body)
    {
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var context = new JsonObject
        {
            ["principal"] = "receipt-provider",
            ["requestId"] = "it-receipt-" + Guid.NewGuid().ToString("N")[..8],
            ["correlationId"] = "11111111-1111-1111-1111-1111111111ff",
            ["scopes"] = new JsonArray("receipt:write"),
            ["transport"] = new JsonObject
            {
                ["signatureVerified"] = true,
                ["signatureVersion"] = 1,
                ["bodySha256"] = bodyHash
            }
        };
        var result = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "receipt", "accept", 1, context.ToJsonString(), body)).RootElement;
        if (result.GetProperty("status").GetString() != "ok")
            throw new Xunit.Sdk.XunitException($"receipt failed: {result.GetRawText()}");
    }

    protected async Task<(string OutboxId, long LeaseVersion)> ClaimOutboxAsync(string owner)
    {
        await using var conn = new NpgsqlConnection(Fixture.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT * FROM delivery.claim_outbox(@o, 1)", conn);
        cmd.Parameters.AddWithValue("o", owner);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "no claimable outbox row");
        return (reader.GetGuid(0).ToString(), reader.GetInt64(1));
    }

    protected async Task RunAutomaticStepAsync(string processId, string expectedStepKey)
    {
        var job = await ClaimForAsync(processId);
        Assert.Equal(expectedStepKey, job["stepKey"]!.GetValue<string>());

        var task = job["task"]!;
        var module = task["module"]!.GetValue<string>();
        var action = task["action"]!.GetValue<string>();
        var version = task["actionVersion"]!.GetValue<int>();
        var processData = job["processData"]!;
        var payload = $$"""{"operationId":"{{processData["operationId"]!.GetValue<string>()}}"}""";
        var scopes = task["requiredPolicy"]!.ToJsonString();
        var executionId = job["executionId"]!.GetValue<string>();
        var ctx = $$"""{"principal":"workflow-worker","requestId":"{{executionId}}","correlationId":"{{executionId}}","processId":"{{processId}}","jobId":"{{job["jobId"]}}","executionId":"{{executionId}}","attemptId":"{{job["attemptId"]}}","scopes":{{scopes}}}""";

        var invoke = JsonDocument.Parse(
            await InvokeAsync(Fixture.WorkerConnection, module, action, version, ctx, payload)).RootElement;
        if (invoke.GetProperty("status").GetString() != "ok")
            throw new Xunit.Sdk.XunitException($"step {expectedStepKey} failed: {invoke.GetRawText()}");
        var outcome = invoke.GetProperty("outcome").GetString();

        var jobId = job["jobId"]!.GetValue<string>();
        var lease = job["leaseVersion"]!.GetValue<long>();
        await Db.ScalarAsync(Fixture.WorkerConnection,
            $"SELECT workflow.finish_job('{jobId}'::uuid,'it-owner',{lease},'{outcome}','{{\"ok\":true}}'::jsonb)");
    }

    private async Task<JsonObject> ClaimForAsync(string processId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var raw = await Db.ScalarAsync(Fixture.WorkerConnection,
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

    protected static string SignedContext(string requestId, string correlationId, string bodyHash)
        => ActionContext("receipt-provider", requestId, correlationId, ["receipt:write"], bodyHash);

    protected static string ActionContext(string principal, string requestId, string correlationId, string[] scopes, string? bodyHash = null)
    {
        var scopesArray = new JsonArray();
        foreach (var scope in scopes)
            scopesArray.Add(scope);
        var context = new JsonObject
        {
            ["principal"] = principal,
            ["requestId"] = requestId,
            ["correlationId"] = correlationId,
            ["scopes"] = scopesArray
        };
        if (bodyHash is not null)
        {
            context["transport"] = new JsonObject
            {
                ["signatureVerified"] = true,
                ["signatureVersion"] = 1,
                ["bodySha256"] = bodyHash
            };
        }
        return context.ToJsonString();
    }

    protected async Task<string> InvokeAsync(string connection, string module, string action, int? version, string context, string payload)
    {
        await using var conn = new NpgsqlConnection(connection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT api.invoke(@m,@a,@v,@ctx::jsonb,@pay::jsonb)::text", conn);
        cmd.Parameters.AddWithValue("m", module);
        cmd.Parameters.AddWithValue("a", action);
        cmd.Parameters.AddWithValue("v", version.HasValue ? version.Value : DBNull.Value);
        cmd.Parameters.AddWithValue("ctx", context);
        cmd.Parameters.AddWithValue("pay", payload);
        return (await cmd.ExecuteScalarAsync())?.ToString() ?? "";
    }
}