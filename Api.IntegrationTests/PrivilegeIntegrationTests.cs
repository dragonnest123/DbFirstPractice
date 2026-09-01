using Npgsql;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public class PrivilegeIntegrationTests
{
    private const string InsufficientPrivilege = "42501";

    private readonly CourseDbFixture _db;

    public PrivilegeIntegrationTests(CourseDbFixture db)
    {
        _db = db;
    }

    [Fact]
    public async Task RuntimeRole_CannotMutateDomainTables()
    {
        var update = await Db.ExecErrorAsync(_db.RuntimeConnection,
            "UPDATE payment.operations SET status='REJECTED' WHERE false");
        Assert.Equal(InsufficientPrivilege, update.SqlState);

        var insert = await Db.ExecErrorAsync(_db.RuntimeConnection,
            "INSERT INTO payment.operations(operation_id, request_id, principal, operation_kind, amount, currency, status) " +
            "VALUES (gen_random_uuid(), 'r', 'p', 'PAYMENT_EXECUTION', 1.00, 'RUB', 'CREATED')");
        Assert.Equal(InsufficientPrivilege, insert.SqlState);

        var delete = await Db.ExecErrorAsync(_db.RuntimeConnection,
            "DELETE FROM payment.operation_events WHERE false");
        Assert.Equal(InsufficientPrivilege, delete.SqlState);
    }

    [Fact]
    public async Task RuntimeRole_CannotWriteCatalog()
    {
        var insert = await Db.ExecErrorAsync(_db.RuntimeConnection,
            "INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, " +
            "request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version) " +
            "VALUES ('x','y',1,'POST','x','y','{}','{}','[]','[]','none','none',1000,false,false,'course-1')");
        Assert.Equal(InsufficientPrivilege, insert.SqlState);

        var update = await Db.ExecErrorAsync(_db.RuntimeConnection,
            "UPDATE api.action_catalog SET enabled=false WHERE false");
        Assert.Equal(InsufficientPrivilege, update.SqlState);
    }

    [Fact]
    public async Task PublicationRole_CannotDmlCatalogOrDomain()
    {
        var update = await Db.ExecErrorAsync(_db.PublicationConnection,
            "UPDATE api.action_catalog SET enabled=false WHERE false");
        Assert.Equal(InsufficientPrivilege, update.SqlState);

        var delete = await Db.ExecErrorAsync(_db.PublicationConnection,
            "DELETE FROM api.action_catalog WHERE false");
        Assert.Equal(InsufficientPrivilege, delete.SqlState);

        var domain = await Db.ExecErrorAsync(_db.PublicationConnection,
            "INSERT INTO payment.operations(operation_id, request_id, principal, operation_kind, amount, currency, status) " +
            "VALUES (gen_random_uuid(), 'r', 'p', 'PAYMENT_EXECUTION', 1.00, 'RUB', 'CREATED')");
        Assert.Equal(InsufficientPrivilege, domain.SqlState);
    }

    [Fact]
    public async Task MigrationRole_CannotUpdateCatalogOrDomain()
    {
        var catalogUpdate = await Db.ExecErrorAsync(_db.MigrationConnection,
            "UPDATE api.action_catalog SET timeout_ms=999 WHERE false");
        Assert.Equal(InsufficientPrivilege, catalogUpdate.SqlState);

        var domainUpdate = await Db.ExecErrorAsync(_db.MigrationConnection,
            "UPDATE payment.operations SET amount=amount WHERE false");
        Assert.Equal(InsufficientPrivilege, domainUpdate.SqlState);

        var eventDelete = await Db.ExecErrorAsync(_db.MigrationConnection,
            "DELETE FROM payment.operation_events WHERE false");
        Assert.Equal(InsufficientPrivilege, eventDelete.SqlState);
    }

    [Fact]
    public async Task MigrationRole_CanApplyLedgerAndSeedCatalog()
    {
        var ledger = await Db.TryExecAsync(_db.MigrationConnection,
            "INSERT INTO public.schema_migrations(filename, checksum) VALUES ('t.sql', repeat('a', 64))");
        Assert.Null(ledger);

        var seed = await Db.TryExecAsync(_db.MigrationConnection,
            "INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, " +
            "request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version) " +
            "VALUES ('priv','seed',1,'POST','priv','seed','{}','{}','[]','[]','none','none',1000,false,false,'course-1')");
        Assert.Null(seed);
    }

    [Theory]
    [InlineData("contract_info")]
    [InlineData("action_definitions")]
    [InlineData("action_dispatches")]
    [InlineData("operations")]
    [InlineData("operation_events")]
    public async Task RuntimeRole_CanReadEvidenceViews(string view)
    {
        var result = await Db.TryExecAsync(_db.RuntimeConnection, $"SELECT * FROM autocheck.{view}");
        Assert.Null(result);
    }
}