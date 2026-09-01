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
        MigrationConnection = $"{baseConnection};Username=course_migration;Password=migration";
        SuperuserConnection = $"{baseConnection};Username=postgres;Password=postgres";
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