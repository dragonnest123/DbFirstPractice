using System.Text.Json;
using Api.Utils;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Api.Tests;

public class EnvelopeDomainErrorTests
{
    [Fact]
    public async Task MissingCodeNormalizesToInternalError()
    {
        using var doc = JsonDocument.Parse("""{"status":"error"}""");

        var result = Envelope.DomainError(doc.RootElement, "c-1", 1);

        var (statusCode, body) = await Execute(result);
        Assert.Equal(500, statusCode);
        using var parsed = JsonDocument.Parse(body);
        Assert.Equal("internal.error", parsed.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task DomainErrorEnvelopeHasExactShapeAndStatus422()
    {
        using var doc = JsonDocument.Parse("""
            {
              "status": "error",
              "code": "probe.forced",
              "message": "forced error",
              "retryable": false,
              "details": {},
              "meta": { "correlationId": "c-1", "actionVersion": 1 }
            }
            """);

        var result = Envelope.DomainError(doc.RootElement, "c-1", 1);

        var (statusCode, body) = await Execute(result);
        Assert.Equal(422, statusCode);

        using var parsed = JsonDocument.Parse(body);
        var root = parsed.RootElement;
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal("probe.forced", root.GetProperty("code").GetString());
        Assert.Equal("forced error", root.GetProperty("message").GetString());
        Assert.False(root.GetProperty("retryable").GetBoolean());
        Assert.Empty(root.GetProperty("details").EnumerateObject());

        var meta = root.GetProperty("meta");
        Assert.Equal("c-1", meta.GetProperty("correlationId").GetString());
        Assert.Equal(1, meta.GetProperty("actionVersion").GetInt32());
    }

    private static async Task<(int StatusCode, string Body)> Execute(IResult result)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddLogging()
                .AddSingleton(new JsonOptions())
                .BuildServiceProvider()
        };
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        return (context.Response.StatusCode, await reader.ReadToEndAsync());
    }
}