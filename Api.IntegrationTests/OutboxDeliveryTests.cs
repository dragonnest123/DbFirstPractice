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
}