using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public sealed class ReceiptSignatureHttpTests : PaymentPerimeterTestBase
{
    private const string SigningKey = "course-test-signing-key-32-bytes-minimum!!!";
    private const string HmacSecret = "test-hmac-secret";

    private readonly WebApplicationFactory<Program> _factory;

    public ReceiptSignatureHttpTests(CourseDbFixture db) : base(db)
    {
        Environment.SetEnvironmentVariable("COURSE_JWT_ISSUER", "moduledev-course");
        Environment.SetEnvironmentVariable("COURSE_JWT_AUDIENCE", "moduledev-api");
        Environment.SetEnvironmentVariable("COURSE_JWT_SIGNING_KEY", SigningKey);
        Environment.SetEnvironmentVariable("PROVIDER_HMAC_SECRET", HmacSecret);
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION", db.RuntimeConnection);
        _factory = new WebApplicationFactory<Program>();
    }

    [Fact]
    public async Task ReceiptAcceptWithoutSignature_Returns403_AndNoInbox()
    {
        var client = _factory.CreateClient();
        var request = BuildReceiptRequest(sign: false);
        var response = await client.SendAsync(request);

        Assert.Equal(403, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("receipt.signature_required", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReceiptAcceptInvalidSignature_Returns401_AndNoInbox()
    {
        var client = _factory.CreateClient();
        var request = BuildReceiptRequest(sign: false);
        request.Headers.TryAddWithoutValidation("X-Provider-Signature", "v1=" + new string('0', 64));
        var response = await client.SendAsync(request);

        Assert.Equal(401, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("signature.invalid", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task ReceiptAcceptValidSignatureUnknownExternal_Returns422()
    {
        var client = _factory.CreateClient();
        var request = BuildReceiptRequest(sign: true);
        var response = await client.SendAsync(request);

        Assert.Equal(422, (int)response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("receipt.external_request_not_found", body.RootElement.GetProperty("code").GetString());

        var inbox = await Db.ScalarAsync(Fixture.SuperuserConnection,
            "SELECT count(*) FROM delivery.inbox WHERE message_id='http-unknown'");
        Assert.Equal("0", inbox);
    }

    private static HttpRequestMessage BuildReceiptRequest(bool sign)
    {
        var receipt = new
        {
            externalRequestId = "http-unknown-external",
            messageId = "http-unknown",
            occurredAt = "2026-09-04T12:00:00Z",
            outcome = "COMPLETED",
            providerPaymentId = "http-unknown",
            version = 1
        };
        var body = JsonSerializer.Serialize(receipt, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/receipt/accept");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {CreateToken("receipt-provider", "integration", "receipt:write")}");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", "http-unknown");
        request.Headers.TryAddWithoutValidation("X-Action-Version", "1");
        if (sign)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HmacSecret));
            var digest = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
            request.Headers.TryAddWithoutValidation("X-Provider-Signature", $"v1={digest}");
        }
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return request;
    }

    private static string CreateToken(string subject, string consumer, string scopes)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(
            issuer: "moduledev-course",
            audience: "moduledev-api",
            claims:
            [
                new Claim("sub", subject),
                new Claim("consumer", consumer),
                new Claim("scope", scopes),
                new Claim("iat", new DateTimeOffset(now).ToUnixTimeSeconds().ToString(), ClaimValueTypes.Integer64)
            ],
            notBefore: now.AddMinutes(-1),
            expires: now.AddHours(1),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}