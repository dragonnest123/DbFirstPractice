using System.Text.Json.Nodes;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class AutocheckViewsTests : PaymentPerimeterTestBase
{
    public AutocheckViewsTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task Week3ViewsExposeRequiredColumns()
    {
        var expected = new HashSet<string>
        {
            "external_requests:external_request_id:text",
            "external_requests:operation_id:uuid",
            "external_requests:state:text",
            "external_requests:payload_hash:text",
            "external_requests:created_at:timestamp with time zone",
            "receipts:message_id:text",
            "receipts:external_request_id:text",
            "receipts:message_version:integer",
            "receipts:outcome:text",
            "receipts:signature_valid:boolean",
            "receipts:body_hash:text",
            "receipts:received_at:timestamp with time zone",
            "receipts:applied_at:timestamp with time zone",
            "outbox:outbox_id:uuid",
            "outbox:external_request_id:text",
            "outbox:state:text",
            "outbox:attempt_count:integer",
            "outbox:next_attempt_at:timestamp with time zone",
            "outbox:last_error_code:text",
            "outbox:created_at:timestamp with time zone",
            "outbox:delivered_at:timestamp with time zone",
            "inbox:message_id:text",
            "inbox:body_hash:text",
            "inbox:state:text",
            "inbox:received_at:timestamp with time zone",
            "inbox:applied_at:timestamp with time zone",
            "decisions:decision_id:uuid",
            "decisions:process_id:uuid",
            "decisions:step_instance_id:uuid",
            "decisions:source:text",
            "decisions:principal:text",
            "decisions:reason_hash:text",
            "decisions:outcome:text",
            "decisions:rule_version:text",
            "decisions:created_at:timestamp with time zone",
        };

        var raw = await Db.ScalarAsync(Fixture.SuperuserConnection,
            """
            SELECT jsonb_agg(table_name || ':' || column_name || ':' || data_type)
            FROM information_schema.columns
            WHERE table_schema = 'autocheck'
              AND table_name IN ('external_requests','receipts','outbox','inbox','decisions')
            """);
        Assert.NotNull(raw);
        var actual = JsonNode.Parse(raw!)!.AsArray()
            .Select(item => item!.GetValue<string>()).ToHashSet();
        Assert.Equal(expected, actual);
    }
}