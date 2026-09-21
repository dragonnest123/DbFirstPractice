using System.Text.Json;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class OperationEventsTests : PaymentPerimeterTestBase
{
    public OperationEventsTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task ContainsSubmittedEvent()
    {
        var operationId = await CreateOperationAsync("PAYMENT_APPROVAL", "100000.00");
        await SubmitAsync(operationId, "it-req-events");

        var result = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "operation", "events", 1,
                """{"principal":"it-principal","requestId":"it-req-events","correlationId":"11111111-1111-1111-1111-111111111111","scopes":["payment:read"]}""",
                $$"""{"operationId":"{{operationId}}"}""")).RootElement;

        Assert.Equal("ok", result.GetProperty("status").GetString());
        Assert.Equal("FOUND", result.GetProperty("outcome").GetString());
        var events = result.GetProperty("result").GetProperty("events").EnumerateArray().ToList();
        Assert.Contains(events, e => e.GetProperty("eventType").GetString() == "OPERATION_SUBMITTED");
    }
}