using System.Text.Json;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class ReviewFlowTests : PaymentPerimeterTestBase
{
    public ReviewFlowTests(CourseDbFixture db) : base(db)
    {
    }

    [Fact]
    public async Task WithinLimit_AutoApprovesWithLimitRuleDecision()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_APPROVAL", "100000.00", "it-req-auto");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "check_limit");
        await RunAutomaticStepAsync(processId, "approve_operation");

        var decision = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT source||'|'||outcome||'|'||rule_version FROM payment.decisions WHERE process_id='{processId}'");
        Assert.Equal("LIMIT_RULE|APPROVED|course-limit-v1", decision);
        var operation = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT status FROM payment.operations WHERE operation_id='{operationId}'");
        Assert.Equal("COMPLETED", operation);
    }

    [Fact]
    public async Task AboveLimit_ManualDecision_IsAuditedAndCompletes()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_APPROVAL", "100000.01", "it-req-manual");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "check_limit");

        var waiting = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT state FROM workflow.process_instance WHERE process_id='{processId}'");
        Assert.Equal("WAITING_MANUAL", waiting);

        var stepId = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT step_instance_id FROM workflow.step_instance WHERE process_id='{processId}' AND step_type='MANUAL'");
        var manual = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "workflow", "manual", 1,
                """{"principal":"reviewer","requestId":"it-manual-1","correlationId":"11111111-1111-1111-1111-11111111111a","scopes":["workflow:manual"]}""",
                $$"""{"processId":"{{processId}}","stepInstanceId":"{{stepId}}","decision":"APPROVED","reason":"it approved"}""")).RootElement;
        Assert.Equal("ok", manual.GetProperty("status").GetString());
        Assert.Equal("MANUAL", manual.GetProperty("result").GetProperty("source").GetString());
        Assert.Equal("reviewer", manual.GetProperty("result").GetProperty("principal").GetString());

        await RunAutomaticStepAsync(processId, "approve_operation");

        var decision = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT source||'|'||principal||'|'||outcome FROM payment.decisions WHERE process_id='{processId}'");
        Assert.Equal("MANUAL|reviewer|APPROVED", decision);
        var operation = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT status FROM payment.operations WHERE operation_id='{operationId}'");
        Assert.Equal("COMPLETED", operation);
    }

    [Fact]
    public async Task ConcurrentDecisionOnCompletedStep_Conflicts()
    {
        var (operationId, processId) = await CreateSubmittedAsync("PAYMENT_APPROVAL", "100000.01", "it-req-conflict");
        await RunAutomaticStepAsync(processId, "validate_operation");
        await RunAutomaticStepAsync(processId, "check_limit");

        var stepId = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT step_instance_id FROM workflow.step_instance WHERE process_id='{processId}' AND step_type='MANUAL'");

        var first = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "workflow", "manual", 1,
                """{"principal":"reviewer","requestId":"it-man-1","correlationId":"11111111-1111-1111-1111-11111111111b","scopes":["workflow:manual"]}""",
                $$"""{"processId":"{{processId}}","stepInstanceId":"{{stepId}}","decision":"APPROVED","reason":"approved once"}""")).RootElement;
        Assert.Equal("ok", first.GetProperty("status").GetString());

        var second = JsonDocument.Parse(
            await InvokeAsync(Fixture.SuperuserConnection, "workflow", "manual", 1,
                """{"principal":"reviewer","requestId":"it-man-2","correlationId":"11111111-1111-1111-1111-11111111111c","scopes":["workflow:manual"]}""",
                $$"""{"processId":"{{processId}}","stepInstanceId":"{{stepId}}","decision":"REJECTED","reason":"changed mind"}""")).RootElement;
        Assert.Equal("error", second.GetProperty("status").GetString());
        Assert.Equal("workflow.decision_conflict", second.GetProperty("code").GetString());

        await RunAutomaticStepAsync(processId, "approve_operation");

        var operation = await Db.ScalarAsync(Fixture.SuperuserConnection,
            $"SELECT status FROM payment.operations WHERE operation_id='{operationId}'");
        Assert.Equal("COMPLETED", operation);
    }
}