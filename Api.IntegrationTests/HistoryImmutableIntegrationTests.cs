using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public class HistoryImmutableIntegrationTests
{
    private readonly CourseDbFixture _db;

    public HistoryImmutableIntegrationTests(CourseDbFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task OperationEvents_AreAppendOnly()
    {
        var operationId = await Db.ScalarAsync(_db.SuperuserConnection,
            "INSERT INTO payment.operations(operation_id, request_id, principal, operation_kind, amount, currency, status) " +
            "VALUES (gen_random_uuid(), 'hist-1', 'p', 'PAYMENT_EXECUTION', 5.00, 'RUB', 'CREATED') RETURNING operation_id::text");
        Assert.NotNull(operationId);

        var eventId = await Db.ScalarAsync(_db.SuperuserConnection,
            $"INSERT INTO payment.operation_events(event_id, operation_id, event_type, payload_hash) " +
            $"VALUES (gen_random_uuid(), '{operationId}', 'OPERATION_CREATED', repeat('b', 64)) RETURNING event_id::text");
        Assert.NotNull(eventId);

        var update = await Db.ExecErrorAsync(_db.SuperuserConnection,
            $"UPDATE payment.operation_events SET event_type='CHANGED' WHERE event_id='{eventId}'");
        Assert.Contains("event.immutable", update.MessageText);

        var delete = await Db.ExecErrorAsync(_db.SuperuserConnection,
            $"DELETE FROM payment.operation_events WHERE event_id='{eventId}'");
        Assert.Contains("event.immutable", delete.MessageText);

        Assert.Equal("1", await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM payment.operation_events WHERE event_id='{eventId}'"));
    }

    [Fact]
    public async Task Operation_IdentityFields_AreImmutable()
    {
        var operationId = await Db.ScalarAsync(_db.SuperuserConnection,
            "INSERT INTO payment.operations(operation_id, request_id, principal, operation_kind, amount, currency, status) " +
            "VALUES (gen_random_uuid(), 'hist-2', 'p', 'PAYMENT_EXECUTION', 5.00, 'RUB', 'CREATED') RETURNING operation_id::text");
        Assert.NotNull(operationId);

        var money = await Db.ExecErrorAsync(_db.SuperuserConnection,
            $"UPDATE payment.operations SET amount=9.99 WHERE operation_id='{operationId}'");
        Assert.Contains("operation.immutable", money.MessageText);

        var kind = await Db.ExecErrorAsync(_db.SuperuserConnection,
            $"UPDATE payment.operations SET operation_kind='PAYMENT_APPROVAL' WHERE operation_id='{operationId}'");
        Assert.Contains("operation.immutable", kind.MessageText);

        var status = await Db.TryExecAsync(_db.SuperuserConnection,
            $"UPDATE payment.operations SET status='PROCESSING' WHERE operation_id='{operationId}'");
        Assert.Null(status);

        var delete = await Db.ExecErrorAsync(_db.SuperuserConnection,
            $"DELETE FROM payment.operations WHERE operation_id='{operationId}'");
        Assert.Contains("operation.immutable", delete.MessageText);
    }
}