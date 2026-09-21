using System.Text.Json;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class PaymentSubmitTests : PaymentPerimeterTestBase
{
    public PaymentSubmitTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task Submit_PinsPaymentProcessingProcess_AndReplaysSameResult()
    {
        var operationId = await CreateOperationAsync("PAYMENT_EXECUTION", "1000.00");
        var first = await SubmitAsync(operationId, "it-req-1");
        var processId = first.GetProperty("result").GetProperty("processId").GetString();

        Assert.Equal("ok", first.GetProperty("status").GetString());
        Assert.Equal("PROCESSING", first.GetProperty("outcome").GetString());
        Assert.Equal("payment-processing", first.GetProperty("result").GetProperty("flowName").GetString());
        Assert.Equal(1, first.GetProperty("result").GetProperty("flowVersion").GetInt32());

        var operation = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT status||'|'||process_id FROM payment.operations WHERE operation_id='{operationId}'");
        Assert.Equal($"PROCESSING|{processId}", operation);

        var repeated = await SubmitAsync(operationId, "it-req-1");
        Assert.Equal(processId, repeated.GetProperty("result").GetProperty("processId").GetString());

        var processes = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT count(*) FROM workflow.process_instance WHERE process_id='{processId}'");
        Assert.Equal("1", processes);
    }

    [Fact]
    public async Task Submit_BindsPaymentApprovalToReviewFlow()
    {
        var operationId = await CreateOperationAsync("PAYMENT_APPROVAL", "100000.00");
        var submit = await SubmitAsync(operationId, "it-req-review");
        Assert.Equal("payment-review", submit.GetProperty("result").GetProperty("flowName").GetString());
    }

    [Fact]
    public async Task Submit_UnknownOperation_ReturnsOperationNotFound()
    {
        var result = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "payment", "submit", 1,
                """{"principal":"it-principal","requestId":"r","correlationId":"11111111-1111-1111-1111-111111111111","scopes":["payment:write"]}""",
                """{"operationId":"00000000-0000-0000-0000-000000000000"}""")).RootElement;
        Assert.Equal("error", result.GetProperty("status").GetString());
        Assert.Equal("operation.not_found", result.GetProperty("code").GetString());
    }
}