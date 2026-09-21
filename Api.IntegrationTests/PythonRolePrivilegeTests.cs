using System.Text.Json.Nodes;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class PythonRolePrivilegeTests : PaymentPerimeterTestBase
{
    public PythonRolePrivilegeTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task PythonRoles_HaveOnlyFixedExecuteAndNoDml()
    {
        var outboxFunctions = await Db.ScalarAsync(Fixture.SuperuserConnection,
            """
            SELECT jsonb_agg(proname || ':' || oidvectortypes(proargtypes) ORDER BY proname)
            FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
            WHERE n.nspname = 'delivery'
              AND has_function_privilege('outbox_dispatcher', p.oid, 'EXECUTE')
            """);
        Assert.NotNull(outboxFunctions);
        var outboxActual = JsonNode.Parse(outboxFunctions!)!.AsArray()
            .Select(item => item!.GetValue<string>()).ToHashSet();
        Assert.Equal(
            new HashSet<string>(["claim_outbox:text, integer", "fail_outbox:uuid, text, bigint, text", "succeed_outbox:uuid, text, bigint, text"]),
            outboxActual);

        var reconcileGranted = await Db.ScalarAsync(Fixture.SuperuserConnection,
            """
            SELECT has_function_privilege('inbox_reconciler', 'delivery.reconcile_inbox(integer)', 'EXECUTE')
                AND NOT has_function_privilege('inbox_reconciler', 'delivery.claim_outbox(text, integer)', 'EXECUTE')
            """);
        Assert.True(bool.Parse(reconcileGranted!));

        var outboxTables = await Db.ScalarAsync(Fixture.SuperuserConnection,
            """
            SELECT count(*) FROM (
              SELECT c.oid FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
              WHERE c.relkind IN ('r','p','v','m','f')
                AND n.nspname NOT IN ('pg_catalog','information_schema')
                AND n.nspname NOT LIKE 'pg_toast%'
                AND (has_table_privilege('outbox_dispatcher', c.oid, 'SELECT')
                  OR has_table_privilege('outbox_dispatcher', c.oid, 'INSERT')
                  OR has_table_privilege('outbox_dispatcher', c.oid, 'UPDATE')
                  OR has_table_privilege('outbox_dispatcher', c.oid, 'DELETE'))
            ) x
            """);
        Assert.Equal("0", outboxTables);
    }
}