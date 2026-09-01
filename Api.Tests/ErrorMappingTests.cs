using Api.Contracts;
using Xunit;

namespace Api.Tests;

public class ErrorMappingTests
{
    [Fact]
    public void DeclaredDomainErrorCodeMapsTo422()
    {
        Assert.Equal(422, ErrorMapping.ToHttpCode("probe.forced"));
        Assert.Equal(422, ErrorMapping.ToHttpCode("payment.insufficient_funds"));
        Assert.Equal(422, ErrorMapping.ToHttpCode("operation.rejected"));
    }

    [Theory]
    [InlineData("access.denied", 403)]
    [InlineData("action.not_found", 404)]
    [InlineData("operation.not_found", 404)]
    [InlineData("idempotency.conflict", 409)]
    [InlineData("idempotency.required", 400)]
    [InlineData("payload.invalid", 422)]
    [InlineData("internal.error", 500)]
    public void FrameworkCodesKeepContractStatuses(string code, int expected)
    {
        Assert.Equal(expected, ErrorMapping.ToHttpCode(code));
    }
}