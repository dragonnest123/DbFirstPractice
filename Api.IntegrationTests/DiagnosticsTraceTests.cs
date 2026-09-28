using System.Text.Json;
using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

/// <summary>
/// Diagnostics must assemble one trace from database relations only: a single
/// identifier has to reach the whole graph, not just the table it was found in.
/// </summary>
[Collection("course-db")]
public sealed class DiagnosticsTraceTests : WorkflowTestBase
{
    public DiagnosticsTraceTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task TraceByJobId_ReachesDeliveryAndWorkflowRelations()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);
        var jobId = job["jobId"]!.GetValue<string>();
        var delivery = await SeedDeliveryLegAsync(process.ProcessId);

        var trace = await TraceAsync(jobId);

        Assert.Equal("FOUND", trace.GetProperty("outcome").GetString());
        var result = trace.GetProperty("result");
        Assert.Equal(jobId, result.GetProperty("jobs").EnumerateArray().First()
            .GetProperty("jobId").GetString());
        Assert.Equal(process.ProcessId, result.GetProperty("process").GetProperty("processId").GetString());
        Assert.NotEmpty(result.GetProperty("steps").EnumerateArray());
        Assert.NotEmpty(result.GetProperty("attempts").EnumerateArray());
        Assert.NotEmpty(result.GetProperty("dispatches").EnumerateArray());
        Assert.Equal(delivery.RequestId, result.GetProperty("operation").GetProperty("requestId").GetString());
        Assert.Equal(delivery.ExternalRequestId, result.GetProperty("outbox").EnumerateArray().First()
            .GetProperty("externalRequestId").GetString());
        Assert.Contains("jobId", result.GetProperty("query").GetProperty("matchedBy")
            .EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public async Task TraceByProcessId_ReachesJobsAndAttempts()
    {
        var process = await StartSeededProcessAsync();
        var job = await ClaimForAsync(process.ProcessId);

        var trace = await TraceAsync(process.ProcessId);

        var result = trace.GetProperty("result");
        Assert.Equal(job["jobId"]!.GetValue<string>(), result.GetProperty("jobs").EnumerateArray()
            .First().GetProperty("jobId").GetString());
        Assert.NotEmpty(result.GetProperty("attempts").EnumerateArray());
    }

    [Fact]
    public async Task TraceByUnknownIdentifier_ReportsNotFound()
    {
        await using var conn = new NpgsqlConnection(_db.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT diagnostics.trace_v1('{\"correlationId\":\"it-trace\"}'::jsonb, $1::jsonb)::text", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = $$"""{"identifier":"{{Guid.NewGuid()}}"}""" });

        using var document = JsonDocument.Parse((await cmd.ExecuteScalarAsync())!.ToString()!);
        Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        Assert.Equal("diagnostics.trace_not_found",
            document.RootElement.GetProperty("code").GetString());
    }

    private async Task<JsonElement> TraceAsync(string identifier)
    {
        await using var conn = new NpgsqlConnection(_db.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT diagnostics.trace_v1('{\"correlationId\":\"it-trace\"}'::jsonb, $1::jsonb)::text", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = $$"""{"identifier":"{{identifier}}"}""" });

        return JsonDocument.Parse((await cmd.ExecuteScalarAsync())!.ToString()!).RootElement;
    }

    private async Task<(string RequestId, string ExternalRequestId)> SeedDeliveryLegAsync(string processId)
    {
        var requestId = "it-trace-" + Guid.NewGuid().ToString("N")[..10];
        var externalRequestId = "it-ext-" + Guid.NewGuid().ToString("N")[..10];
        var operationId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var bodyHash = new string('a', 64);

        await using var conn = new NpgsqlConnection(_db.SuperuserConnection);
        await conn.OpenAsync();
        await ExecuteAsync(conn, """
            INSERT INTO payment.operations
                (operation_id, request_id, principal, operation_kind, amount, currency, status, process_id)
            VALUES ($1, $2, 'it-trace', 'PAYMENT_EXECUTION', 10.00, 'RUB', 'PROCESSING', $3)
            """, operationId, requestId, Guid.Parse(processId));
        await ExecuteAsync(conn, """
            INSERT INTO api.action_dispatches
                (correlation_id, request_id, module, action, version, principal, payload_hash, status, outcome)
            VALUES ($1, $2, 'payment', 'submit', 1, 'it-trace', $3, 'OK', 'ACCEPTED')
            """, correlationId, requestId, bodyHash);
        await ExecuteAsync(conn, """
            INSERT INTO delivery.external_request
                (external_request_id, operation_id, correlation_id, amount, currency, state, payload_hash)
            VALUES ($1, $2, $3, 10.00, 'RUB', 'SENT', $4)
            """, externalRequestId, operationId, correlationId, bodyHash);
        await ExecuteAsync(conn, """
            INSERT INTO delivery.outbox (external_request_id, correlation_id, state)
            VALUES ($1, $2, 'PENDING')
            """, externalRequestId, correlationId);

        return (requestId, externalRequestId);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, params object[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var parameter in parameters)
            cmd.Parameters.Add(new NpgsqlParameter { Value = parameter });
        await cmd.ExecuteNonQueryAsync();
    }
}
