using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Cli.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public class DomainErrorHttpIntegrationTests
{
    private const string SigningKey = "course-test-signing-key-32-bytes-minimum!!!";

    private readonly CourseDbFixture _db;
    private readonly WebApplicationFactory<Program> _factory;

    public DomainErrorHttpIntegrationTests(CourseDbFixture db)
    {
        _db = db;

        var migrationFile = Path.Combine(TestPaths.RepoRoot, "task", "week1", "autocheck", "fixtures", "migrations", "900_opencheck_probe.sql");
        var migrationSql = File.ReadAllText(migrationFile);
        new MigrationService(_db.MigrationConnection).ApplyMigrationAsync(
            $"opencheck_{Guid.NewGuid():N}.sql",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(migrationSql))).ToLowerInvariant(),
            migrationSql).GetAwaiter().GetResult();

        var manifestFile = Path.Combine(TestPaths.RepoRoot, "task", "week1", "autocheck", "fixtures", "manifests", "opencheck-probe-v1.action.json");
        new PublicationService(_db.PublicationConnection).PublishAsync(
            File.ReadAllText(manifestFile)).GetAwaiter().GetResult();

        Environment.SetEnvironmentVariable("COURSE_JWT_ISSUER", "moduledev-course");
        Environment.SetEnvironmentVariable("COURSE_JWT_AUDIENCE", "moduledev-api");
        Environment.SetEnvironmentVariable("COURSE_JWT_SIGNING_KEY", SigningKey);
        Environment.SetEnvironmentVariable("POSTGRES_CONNECTION", _db.RuntimeConnection);

        _factory = new WebApplicationFactory<Program>();
    }

    [Fact]
    public async Task ForcedDomainError_Returns422_ExactEnvelope_AndRollsBack()
    {
        var marker = $"err-{Guid.NewGuid():N}";
        var response = await _factory.CreateClient().SendAsync(BuildProbeRequest("error", marker));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(422, (int)response.StatusCode);
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal("probe.forced", root.GetProperty("code").GetString());
        Assert.Equal("forced error", root.GetProperty("message").GetString());
        Assert.False(root.GetProperty("retryable").GetBoolean());
        Assert.Empty(root.GetProperty("details").EnumerateObject());

        var meta = root.GetProperty("meta");
        Assert.True(Guid.TryParse(meta.GetProperty("correlationId").GetString(), out _));
        Assert.Equal(1, meta.GetProperty("actionVersion").GetInt32());

        var canary = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM opencheck.canary WHERE marker='{marker}'");
        Assert.Equal("0", canary);
    }

    [Fact]
    public async Task SuccessfulProbe_Returns200_AndCommits()
    {
        var marker = $"ok-{Guid.NewGuid():N}";
        var response = await _factory.CreateClient().SendAsync(BuildProbeRequest("ok", marker));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(200, (int)response.StatusCode);
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("APPLIED", root.GetProperty("outcome").GetString());
        Assert.True(root.GetProperty("result").GetProperty("stored").GetBoolean());

        var canary = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM opencheck.canary WHERE marker='{marker}'");
        Assert.Equal("1", canary);
    }

    [Fact]
    public async Task UnknownOutcome_Returns500_ContractViolation_AndRollsBack()
    {
        var marker = $"viol-{Guid.NewGuid():N}";
        var response = await _factory.CreateClient().SendAsync(BuildProbeRequest("unknown_outcome", marker));

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = body.RootElement;

        Assert.Equal(500, (int)response.StatusCode);
        Assert.Equal("action.contract_violation", root.GetProperty("code").GetString());

        var canary = await Db.ScalarAsync(_db.SuperuserConnection,
            $"SELECT count(*) FROM opencheck.canary WHERE marker='{marker}'");
        Assert.Equal("0", canary);
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

    private static HttpRequestMessage BuildProbeRequest(string mode, string value)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/opencheck/probe");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {CreateToken("candidate-client", "web", "workflow:execute payment:write payment:read")}");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", $"key-{Guid.NewGuid():N}");
        request.Content = JsonContent.Create(new { mode, value });
        return request;
    }

    [Fact]
    public async Task ForcedDomainError_RequiresAuth_AndIdempotencyKey()
    {
        var marker = $"auth-{Guid.NewGuid():N}";
        var client = _factory.CreateClient();

        var noAuth = await client.PostAsJsonAsync("/api/opencheck/probe", new { mode = "error", value = marker });
        Assert.Equal(401, (int)noAuth.StatusCode);

        var request = BuildProbeRequest("error", marker);
        request.Headers.Remove("Idempotency-Key");
        var noKey = await client.SendAsync(request);
        Assert.Equal(400, (int)noKey.StatusCode);
    }
}