using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class OutboxDeliveryTests : PaymentPerimeterTestBase
{
    public OutboxDeliveryTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task PrepareExternal_CreatesExactlyOneExternalRequestAndOutbox()
    {
        var operationId = await CreateOperationAsync("PAYMENT_EXECUTION", "1000.00");
        await SubmitAsync(operationId, "it-req-prep");
        await PrepareExternalDirectAsync(operationId);
        await PrepareExternalDirectAsync(operationId);

        var external = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT count(*) FROM delivery.external_request WHERE operation_id='{operationId}'");
        Assert.Equal("1", external);

        var outbox = await Db.ScalarAsync(Fixture.SuperuserConnection,
            "SELECT count(*) FROM delivery.outbox o JOIN delivery.external_request e ON e.external_request_id=o.external_request_id "
            + $"WHERE e.operation_id='{operationId}'");
        Assert.Equal("1", outbox);
    }

    [Fact]
    public async Task ClaimSucceedFail_TraversesOutboxLifecycle()
    {
        await PrepareAsync("it-req-ob1");
        var (outboxId, leaseVersion) = await ClaimOutboxAsync("outbox_dispatcher");
        await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT delivery.succeed_outbox('{outboxId}'::uuid,'outbox_dispatcher',{leaseVersion},'provider-1')::text");
        var delivered = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.outbox WHERE outbox_id='{outboxId}'");
        Assert.Equal("DELIVERED", delivered);

        await PrepareAsync("it-req-ob2");
        var (outboxId2, leaseVersion2) = await ClaimOutboxAsync("outbox_dispatcher");
        await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT delivery.fail_outbox('{outboxId2}'::uuid,'outbox_dispatcher',{leaseVersion2},'http.429.retryable')::text");
        var retry = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state||'|'||last_error_code FROM delivery.outbox WHERE outbox_id='{outboxId2}'");
        Assert.Equal("RETRY_WAIT|http.429.retryable", retry);

        await PrepareAsync("it-req-ob3");
        var (outboxId3, leaseVersion3) = await ClaimOutboxAsync("outbox_dispatcher");
        await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT delivery.fail_outbox('{outboxId3}'::uuid,'outbox_dispatcher',{leaseVersion3},'http.400.terminal')::text");
        var dead = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.outbox WHERE outbox_id='{outboxId3}'");
        Assert.Equal("DEAD", dead);
    }

    [Fact]
    public async Task ConfirmedOutbox_NotRegressedOrReclaimed()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-confirm");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        var externalId = await ExternalIdAsync(operationId);
        var messageId = "it-confirm-" + Guid.NewGuid().ToString("N")[..10];
        await AcceptReceiptAsync(ReceiptBody(externalId, messageId, "COMPLETED"));

        var outbox = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT outbox_id FROM delivery.outbox WHERE external_request_id='{externalId}'");
        Assert.NotNull(outbox);

        await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT delivery.succeed_outbox('{outbox}'::uuid,'outbox_dispatcher',1,'provider-x')::text");
        await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT delivery.fail_outbox('{outbox}'::uuid,'outbox_dispatcher',1,'http.500.retryable')::text");

        var state = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.outbox WHERE outbox_id='{outbox}'");
        Assert.Equal("CONFIRMED", state);

        var reclaimed = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT count(*) FROM delivery.claim_outbox('outbox_dispatcher', 100) c WHERE c.outbox_id='{outbox}'::uuid");
        Assert.Equal("0", reclaimed);
    }

    [Fact]
    public async Task ConfirmWinsOverStaleFail_WhileDispatcherHoldsFence()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_EXECUTION", "1000.00", "it-req-failrace");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "prepare_external_request");
        var externalId = await ExternalIdAsync(operationId);

        var setup = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"UPDATE delivery.outbox SET state='LEASED', lease_owner='it-owner', " +
            "lease_version=lease_version+1, attempt_count=attempt_count+1, " +
            "lease_until=clock_timestamp()+interval '30 seconds' " +
            $"WHERE external_request_id='{externalId}' AND state IN ('PENDING','RETRY_WAIT') " +
            "RETURNING outbox_id::text||'|'||lease_version");
        Assert.NotNull(setup);
        var parts = setup!.Split('|');
        var outboxId = parts[0];
        var leaseVersion = long.Parse(parts[1]);

        var messageId = "it-failrace-" + Guid.NewGuid().ToString("N")[..10];
        var body = ReceiptBody(externalId, messageId, "COMPLETED");
        var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        await using var confirm = new NpgsqlConnection(Fixture.SuperuserConnection);
        await confirm.OpenAsync();
        await using var confirmTx = await confirm.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
                         "SELECT api.invoke(@m,@a,@v,@ctx::jsonb,@pay::jsonb)::text", confirm, confirmTx))
        {
            cmd.Parameters.AddWithValue("m", "receipt");
            cmd.Parameters.AddWithValue("a", "accept");
            cmd.Parameters.AddWithValue("v", 1);
            cmd.Parameters.AddWithValue("ctx", SignedContext(
                "r", "11111111-1111-1111-1111-11111111111a", bodyHash));
            cmd.Parameters.AddWithValue("pay", body);
            var raw = (await cmd.ExecuteScalarAsync())?.ToString();
            using var accepted = JsonDocument.Parse(raw!);
            Assert.Equal("ok", accepted.RootElement.GetProperty("status").GetString());
        }

        var failTask = Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT delivery.fail_outbox('{outboxId}'::uuid,'it-owner',{leaseVersion},'http.500.retryable')::text");
        await Task.Delay(150);
        await confirmTx.CommitAsync();

        var failRaw = await failTask;
        using var fail = JsonDocument.Parse(failRaw!);
        Assert.Equal("stale", fail.RootElement.GetProperty("status").GetString());

        Assert.Equal("CONFIRMED", await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.outbox WHERE outbox_id='{outboxId}'"));
        Assert.Equal("1", await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT attempt_count FROM delivery.outbox WHERE outbox_id='{outboxId}'"));
        Assert.Equal("CONFIRMED", await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.external_request WHERE external_request_id='{externalId}'"));
        Assert.Equal("RECEIVED", await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM delivery.inbox WHERE message_id='{messageId}'"));
    }
}