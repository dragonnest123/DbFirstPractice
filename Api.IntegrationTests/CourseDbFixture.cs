using System.Security.Cryptography;
using System.Text;
using Cli.Services;
using Testcontainers;
using Testcontainers.PostgreSql;
using Xunit;

namespace Api.IntegrationTests;

[CollectionDefinition("course-db")]
public sealed class CourseDbCollection : ICollectionFixture<CourseDbFixture>;

public sealed class CourseDbFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;

    public CourseDbFixture()
    {
        var migrations = Path.Combine(TestPaths.RepoRoot, "Api", "Migrations");
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16")
            .WithDatabase("course")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .WithBindMount(migrations, "/docker-entrypoint-initdb.d")
            .Build();
    }

    public string RuntimeConnection { get; private set; } = null!;

    public string PublicationConnection { get; private set; } = null!;

    public string WorkerConnection { get; private set; } = null!;

    public string MigrationConnection { get; private set; } = null!;

    public string SuperuserConnection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var host = _postgres.Hostname;
        var port = _postgres.GetMappedPublicPort(5432);
        var baseConnection = $"Host={host};Port={port};Database=course;Include Error Detail=false";
        RuntimeConnection = $"{baseConnection};Username=course_runtime;Password=runtime";
        PublicationConnection = $"{baseConnection};Username=course_publication;Password=publication";
        WorkerConnection = $"{baseConnection};Username=workflow_worker;Password=worker";
        MigrationConnection = $"{baseConnection};Username=course_migration;Password=migration";
        SuperuserConnection = $"{baseConnection};Username=postgres;Password=postgres";

        var fixtures = Path.Combine(TestPaths.RepoRoot, "task", "week1", "autocheck", "fixtures");
        await ApplyFileAsync(Path.Combine(fixtures, "migrations", "900_opencheck_probe.sql"));
        await ApplySqlAsync("""
            CREATE SCHEMA IF NOT EXISTS pubtest_ok;
            CREATE OR REPLACE FUNCTION pubtest_ok.probe(p_context jsonb, p_payload jsonb)
            RETURNS jsonb LANGUAGE plpgsql SECURITY DEFINER
            AS $$ BEGIN RETURN jsonb_build_object('status','ok','outcome','OK'); END $$;
            """);

        var manifest = Path.Combine(fixtures, "manifests", "opencheck-probe-v1.action.json");
        await new PublicationService(PublicationConnection).PublishAsync(
            await File.ReadAllTextAsync(manifest));
    }

    private async Task ApplyFileAsync(string file)
    {
        await ApplySqlAsync(await File.ReadAllTextAsync(file));
    }

    private async Task ApplySqlAsync(string sql)
    {
        var checksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sql))).ToLowerInvariant();
        await new MigrationService(MigrationConnection).ApplyMigrationAsync(
            $"{Guid.NewGuid():N}.sql", checksum, sql);
    }

    public Task DisposeAsync() => _postgres.StopAsync();
}

public static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Api", "Migrations")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repository root not found");
    }
}