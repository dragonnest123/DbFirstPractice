using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class ReceiptAcceptTests : PaymentPerimeterTestBase
{
    public ReceiptAcceptTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task SignatureBoundary_AcceptsDuplicateAndConflict()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-rc");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        var waiting = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{processId}'");
        Assert.Equal("WAITING_SIGNAL", waiting);

        var externalId = await ExternalIdAsync(operationId);
        var messageId = "it-msg-" + Guid.NewGuid().ToString("N")[..10];
        var body = ReceiptBody(externalId, messageId, "COMPLETED");
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        var noSignature = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "receipt", "accept", 1,
                ActionContext("receipt-provider", "r-nosig", "11111111-1111-1111-1111-111111111114", ["receipt:write"]),
                body)).RootElement;
        Assert.Equal("receipt.signature_required", noSignature.GetProperty("code").GetString());

        var signed = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "receipt", "accept", 1,
                SignedContext("r-1", "11111111-1111-1111-1111-111111111115", bodyHash),
                body)).RootElement;
        Assert.Equal("ok", signed.GetProperty("status").GetString());
        Assert.Equal("RECEIVED", signed.GetProperty("outcome").GetString());

        var inbox = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.inbox WHERE message_id='{messageId}'");
        Assert.Equal("RECEIVED", inbox);
        var outboxConfirmed = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT o.state FROM delivery.outbox o JOIN delivery.external_request e ON e.external_request_id=o.external_request_id WHERE e.external_request_id='{externalId}'");
        Assert.Equal("CONFIRMED", outboxConfirmed);

        var duplicate = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "receipt", "accept", 1,
                SignedContext("r-2", "11111111-1111-1111-1111-111111111116", bodyHash),
                body)).RootElement;
        Assert.Equal("DUPLICATE", duplicate.GetProperty("outcome").GetString());

        var conflicting = body.Replace("\"COMPLETED\"", "\"REJECTED\"", StringComparison.Ordinal);
        var conflictHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conflicting))).ToLowerInvariant();
        var conflict = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "receipt", "accept", 1,
                SignedContext("r-3", "11111111-1111-1111-1111-111111111117", conflictHash),
                conflicting)).RootElement;
        Assert.Equal("idempotency.conflict", conflict.GetProperty("code").GetString());
    }

    [Fact]
    public async Task UnknownExternalRequest_ReturnsNotFound()
    {
        var body = """{"externalRequestId":"unknown-ext","messageId":"m-unknown","occurredAt":"2026-09-04T12:00:00Z","outcome":"COMPLETED","providerPaymentId":"m-unknown","version":1}""";
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
        var result = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "receipt", "accept", 1,
                SignedContext("r", "11111111-1111-1111-1111-111111111118", bodyHash),
                body)).RootElement;
        Assert.Equal("receipt.external_request_not_found", result.GetProperty("code").GetString());
    }

    [Fact]
    public async Task EarlyReceipt_AppliedWhenProcessEntersWait()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-early");
        await RunAutomaticStepAsync(processId, "validate_operation");

        await PrepareExternalDirectAsync(operationId);
        var externalId = await ExternalIdAsync(operationId);

        var messageId = "it-early-" + Guid.NewGuid().ToString("N")[..10];
        var body = ReceiptBody(externalId, messageId, "COMPLETED");
        await AcceptReceiptAsync(body);

        var inbox = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.inbox WHERE message_id='{messageId}'");
        Assert.Equal("RECEIVED", inbox);

        await RunAutomaticStepAsync(processId, "prepare_external_request");

        var applied = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.inbox WHERE message_id='{messageId}'");
        Assert.Equal("APPLIED", applied);
        var signal = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT status FROM workflow.workflow_signal WHERE message_id='{messageId}'");
        Assert.Equal("APPLIED", signal);

        var nextJob = await Db.ScalarAsync(Fixture.SuperuserConnection,
            "SELECT count(*) FROM workflow.workflow_job j JOIN workflow.step_instance s ON s.step_instance_id=j.step_instance_id "
            + $"WHERE s.process_id='{processId}' AND s.step_key='apply_receipt' AND j.state IN ('READY','RETRY_WAIT')");
        Assert.Equal("1", nextJob);
    }

    [Fact]
    public async Task PaymentExecution_CompletesAfterAppliedReceipt()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-e2e");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");

        var externalId = await ExternalIdAsync(operationId);
        var messageId = "it-e2e-" + Guid.NewGuid().ToString("N")[..10];
        await AcceptReceiptAsync(ReceiptBody(externalId, messageId, "COMPLETED"));

        await Db.ScalarAsync(Fixture.SuperuserConnection, "SELECT delivery.reconcile_inbox(100)::text");

        var inboxState = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.inbox WHERE message_id='{messageId}'");
        Assert.Equal("APPLIED", inboxState);

        await RunAutomaticStepAsync(processId, "apply_receipt");
        await RunAutomaticStepAsync(processId, "complete_operation");

        var operation = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT status FROM payment.operations WHERE operation_id='{operationId}'");
        Assert.Equal("COMPLETED", operation);
        var processState = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{processId}'");
        Assert.Equal("COMPLETED", processState);
    }

    [Fact]
    public async Task PaymentExecution_RejectsOnRejectedReceipt()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-reject");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");

        var externalId = await ExternalIdAsync(operationId);
        var messageId = "it-reject-" + Guid.NewGuid().ToString("N")[..10];
        await AcceptReceiptAsync(ReceiptBody(externalId, messageId, "REJECTED"));
        await Db.ScalarAsync(Fixture.SuperuserConnection, "SELECT delivery.reconcile_inbox(100)::text");

        var inbox = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.inbox WHERE message_id='{messageId}'");
        Assert.Equal("APPLIED", inbox);

        await RunAutomaticStepAsync(processId, "apply_receipt");
        await RunAutomaticStepAsync(processId, "reject_operation");

        var operation = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT status FROM payment.operations WHERE operation_id='{operationId}'");
        Assert.Equal("REJECTED", operation);
    }
}