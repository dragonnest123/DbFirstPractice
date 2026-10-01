using System.Text.Json;
using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

/// <summary>
/// A DEAD outbox row is not by itself a stalled operation: right after the last
/// attempt failed, delivery is still within its normal budget. The operation is
/// reported only once it has been waiting past the threshold, and it leaves the
/// sample as soon as the receipt is applied.
/// </summary>
[Collection("course-db")]
public sealed class DiagnosticsStalledTests : PaymentPerimeterTestBase
{
    private const int StalledAfterSeconds = 10;

    public DiagnosticsStalledTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task FreshDeadOutbox_IsNotReportedAsStalled()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-stall-fresh");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        await ExhaustDeliveryAsync(operationId);

        var items = await StalledOperationsAsync();

        Assert.DoesNotContain(items, id => id == operationId);
    }

    [Fact]
    public async Task OperationPastThreshold_IsReportedWithPayloadSchema()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-stall-age");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        var externalId = await ExternalIdAsync(operationId);
        await ExhaustDeliveryAsync(operationId);

        await SetDeadAtAsync(externalId, $"clock_timestamp() - interval '{StalledAfterSeconds + 5} seconds'");

        var items = await StalledOperationsAsync();

        Assert.Contains(items, id => id == operationId);
    }

    [Fact]
    public async Task JustBelowThreshold_IsNotReported()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-stall-under");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        var externalId = await ExternalIdAsync(operationId);
        await ExhaustDeliveryAsync(operationId);

        await SetDeadAtAsync(externalId, $"clock_timestamp() - interval '{StalledAfterSeconds - 3} seconds'");

        var items = await StalledOperationsAsync();

        Assert.DoesNotContain(items, id => id == operationId);
    }

    [Fact]
    public async Task AppliedReceipt_RemovesOperationFromStalledSample()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-stall-apply");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        var externalId = await ExternalIdAsync(operationId);
        await ExhaustDeliveryAsync(operationId);
        await SetDeadAtAsync(externalId, $"clock_timestamp() - interval '{StalledAfterSeconds + 5} seconds'");

        Assert.Contains(await StalledOperationsAsync(), id => id == operationId);

        var outboxBefore = await OutboxCountAsync(operationId);
        var externalBefore = await ExternalCountAsync(operationId);

        var messageId = "it-stalled-" + Guid.NewGuid().ToString("N")[..10];
        await AcceptReceiptAsync(ReceiptBody(externalId, messageId, "COMPLETED"));
        await Db.ScalarAsync(Fixture.SuperuserConnection, "SELECT delivery.reconcile_inbox(100)::text");

        var items = await StalledOperationsAsync();
        Assert.DoesNotContain(items, id => id == operationId);

        // Reading the sample is a read: it must not create work of its own.
        Assert.Equal(outboxBefore, await OutboxCountAsync(operationId));
        Assert.Equal(externalBefore, await ExternalCountAsync(operationId));
    }

    [Fact]
    public async Task StalledSample_IsOrderedAndUniquePerOperation()
    {
        var first = await CreateStalledOperationAsync("it-req-stall-order-a");
        var second = await CreateStalledOperationAsync("it-req-stall-order-b");

        var items = await StalledOperationsAsync();

        Assert.Contains(items, id => id == first);
        Assert.Contains(items, id => id == second);
        Assert.Equal(items.OrderBy(id => id, StringComparer.Ordinal), items);

        var reported = items.Where(id => id == first || id == second).ToList();
        Assert.Equal(reported.Count, reported.Distinct(StringComparer.Ordinal).Count());
    }

    private async Task<string> CreateStalledOperationAsync(string requestId)
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", requestId);
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        await ExhaustDeliveryAsync(operationId);
        await SetDeadAtAsync(await ExternalIdAsync(operationId),
            $"clock_timestamp() - interval '{StalledAfterSeconds + 5} seconds'");
        return operationId;
    }

    /// <summary>Drives the real delivery loop until the row is DEAD, backoff waits removed.</summary>
    private async Task ExhaustDeliveryAsync(string operationId)
    {
        var externalId = await ExternalIdAsync(operationId);

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            var lease = await Db.ScalarAsync(Fixture.SuperuserConnection,
                "UPDATE delivery.outbox SET state='LEASED', lease_owner='it-stalled', " +
                "lease_version=lease_version+1, attempt_count=attempt_count+1, next_attempt_at=NULL, " +
                "lease_until=clock_timestamp()+interval '30 seconds' " +
                $"WHERE external_request_id='{externalId}' AND state IN ('PENDING','RETRY_WAIT') " +
                "RETURNING outbox_id::text||'|'||lease_version");
            if (lease is null)
                break;

            var parts = lease.Split('|');
            await Db.ScalarAsync(Fixture.SuperuserConnection,
                $"SELECT delivery.fail_outbox('{parts[0]}'::uuid,'it-stalled',{parts[1]},'transport.error.retryable')::text");

            if (await OutboxStateAsync(externalId) == "DEAD")
                return;
        }

        Assert.Equal("DEAD", await OutboxStateAsync(externalId));
    }

    private async Task<string> OutboxStateAsync(string externalId) =>
        (await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.outbox WHERE external_request_id='{externalId}'"))!;

    /// <summary>Backdates the stored fact so the threshold can be crossed without waiting.</summary>
    private async Task SetDeadAtAsync(string externalId, string expression)
    {
        var error = await Db.TryExecAsync(Fixture.SuperuserConnection,
            $"UPDATE delivery.outbox SET dead_at={expression} WHERE external_request_id='{externalId}'");
        Assert.Null(error);
    }

    private async Task<string> OutboxCountAsync(string operationId) =>
        (await Db.ScalarAsync(Fixture.SuperuserConnection,
            "SELECT count(*) FROM delivery.outbox o JOIN delivery.external_request e ON e.external_request_id=o.external_request_id " +
            $"WHERE e.operation_id='{operationId}'"))!;

    private async Task<string> ExternalCountAsync(string operationId) =>
        (await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT count(*) FROM delivery.external_request WHERE operation_id='{operationId}'"))!;

    private async Task<List<string>> StalledOperationsAsync()
    {
        await using var conn = new NpgsqlConnection(Fixture.SuperuserConnection);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT diagnostics.stalled_v1('{\"correlationId\":\"it-stalled\"}'::jsonb, '{}'::jsonb)::text", conn);

        using var document = JsonDocument.Parse((await cmd.ExecuteScalarAsync())!.ToString()!);
        Assert.Equal("ok", document.RootElement.GetProperty("status").GetString());

        return document.RootElement.GetProperty("result").GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("operationId").GetString()!)
            .ToList();
    }
}