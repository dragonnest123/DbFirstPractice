using System.Text.Json;
using Cli.Services;
using Xunit;

namespace Api.IntegrationTests;

[Collection("course-db")]
public class PublicationIntegrationTests
{
    private const string TargetSchema = "pubtest_ok";
    private const string TargetFunction = "probe";

    private readonly CourseDbFixture _db;

    public PublicationIntegrationTests(CourseDbFixture db)
    {
        _db = db;
    }

    private PublicationService Publication() => new(_db.PublicationConnection);

    [Fact]
    public async Task Publish_NewVersion_Succeeds()
    {
        var manifest = BuildManifest(module: "pubtest", action: "ok", version: 1, isDefault: true, timeoutMs: 2000);

        await Publication().PublishAsync(manifest);

        var count = await Db.ScalarAsync(_db.SuperuserConnection,
            "SELECT count(*) FROM api.action_catalog WHERE module='pubtest' AND action='ok' AND version=1");
        Assert.Equal("1", count);
    }

    [Fact]
    public async Task Publish_SameVersion_IsIdempotent()
    {
        var manifest = BuildManifest(module: "pubtest", action: "idem", version: 1, isDefault: true, timeoutMs: 2000);

        await Publication().PublishAsync(manifest);
        await Publication().PublishAsync(manifest);
    }

    [Fact]
    public async Task Publish_ChangedVersion_Conflicts()
    {
        var manifest = BuildManifest(module: "pubtest", action: "immutable", version: 1, isDefault: true, timeoutMs: 2000);
        var changed = BuildManifest(module: "pubtest", action: "immutable", version: 1, isDefault: true, timeoutMs: 2001);

        await Publication().PublishAsync(manifest);
        var ex = await Assert.ThrowsAsync<PublicationException>(() => Publication().PublishAsync(changed));

        Assert.Equal("manifest.conflict", ex.Code);
    }

    [Fact]
    public async Task Publish_SecondDefault_Conflicts()
    {
        var v1 = BuildManifest(module: "pubtest", action: "defaults", version: 1, isDefault: true, timeoutMs: 2000);
        var v2 = BuildManifest(module: "pubtest", action: "defaults", version: 2, isDefault: true, timeoutMs: 2000);

        await Publication().PublishAsync(v1);
        var ex = await Assert.ThrowsAsync<PublicationException>(() => Publication().PublishAsync(v2));

        Assert.Equal("manifest.conflict", ex.Code);
    }

    [Theory]
    [InlineData("pubtest_notowned", "postgres")]
    [InlineData("pubtest_otherowner", "course_migration")]
    public async Task Publish_TargetNotOwnedByCourseTarget_Rejected(string schema, string ownerRole)
    {
        var create = $"""
            CREATE SCHEMA IF NOT EXISTS {schema};
            CREATE OR REPLACE FUNCTION {schema}.probe(p_context jsonb, p_payload jsonb)
            RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
            AS $$ BEGIN RETURN jsonb_build_object('status','ok'); END $$;
            ALTER FUNCTION {schema}.probe(jsonb,jsonb) OWNER TO {ownerRole};
            """;
        Assert.Null(await Db.TryExecAsync(_db.SuperuserConnection, create));

        var manifest = BuildManifest(module: "pubtest", action: "badowner", version: 1, isDefault: true, timeoutMs: 2000,
            targetSchema: schema, targetFunction: "probe");

        var ex = await Assert.ThrowsAsync<PublicationException>(() => Publication().PublishAsync(manifest));

        Assert.Equal("manifest.invalid", ex.Code);
        Assert.Contains("course_target", ex.Message);
    }

    [Fact]
    public async Task Publish_MissingTarget_Rejected()
    {
        var manifest = BuildManifest(module: "pubtest", action: "missing", version: 1, isDefault: true, timeoutMs: 2000,
            targetSchema: "pubtest_notowned", targetFunction: "does_not_exist");

        var ex = await Assert.ThrowsAsync<PublicationException>(() => Publication().PublishAsync(manifest));

        Assert.Equal("manifest.invalid", ex.Code);
    }

    [Fact]
    public async Task ActivateAndDisable_RoundTrip()
    {
        var v1 = BuildManifest(module: "pubtest", action: "lifecycle", version: 1, isDefault: true, timeoutMs: 2000);
        var v2 = BuildManifest(module: "pubtest", action: "lifecycle", version: 2, isDefault: false, timeoutMs: 2000);

        var pub = Publication();
        await pub.PublishAsync(v1);
        await pub.PublishAsync(v2);

        await pub.ActivateAsync("pubtest", "lifecycle", 2);
        Assert.Equal("1", await Db.ScalarAsync(_db.SuperuserConnection,
            "SELECT count(*) FROM api.action_catalog WHERE module='pubtest' AND action='lifecycle' AND is_default AND version=2"));

        await pub.DisableAsync("pubtest", "lifecycle", 2, 1);
        Assert.Equal("1", await Db.ScalarAsync(_db.SuperuserConnection,
            "SELECT count(*) FROM api.action_catalog WHERE module='pubtest' AND action='lifecycle' AND is_default AND version=1"));
        Assert.Equal("0", await Db.ScalarAsync(_db.SuperuserConnection,
            "SELECT count(*) FROM api.action_catalog WHERE module='pubtest' AND action='lifecycle' AND enabled AND version=2"));
    }

    private static Dictionary<string, object?> Schema() => new()
    {
        ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
        ["type"] = "object"
    };

    private static string BuildManifest(
        string module, string action, int version, bool isDefault, int timeoutMs,
        string targetSchema = TargetSchema, string targetFunction = TargetFunction)
    {
        return JsonSerializer.Serialize(new
        {
            contract_version = "course-1",
            module,
            action,
            version,
            http_method = "POST",
            target_schema = targetSchema,
            target_function = targetFunction,
            request_schema = Schema(),
            response_schema = Schema(),
            outcomes = new[] { "OK" },
            required_policy = new[] { "payment:write" },
            idempotency_mode = "none",
            idempotency_scope = "none",
            timeout_ms = timeoutMs,
            enabled = true,
            is_default = isDefault
        });
    }
}