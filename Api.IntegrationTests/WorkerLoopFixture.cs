using Testcontainers;
using Testcontainers.PostgreSql;
using Xunit;

namespace Api.IntegrationTests;

[CollectionDefinition("worker-loop")]
public sealed class WorkerLoopCollection : ICollectionFixture<WorkerLoopFixture>;

/// <summary>Dedicated database for running the real WorkerLoop in isolation.</summary>
public sealed class WorkerLoopFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres;

    public WorkerLoopFixture()
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

    public string WorkerConnection { get; private set; } = null!;

    public string SuperuserConnection { get; private set; } = null!;

    public string PublicationConnection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var host = _postgres.Hostname;
        var port = _postgres.GetMappedPublicPort(5432);
        var baseConnection = $"Host={host};Port={port};Database=course;Include Error Detail=false";
        WorkerConnection = $"{baseConnection};Username=workflow_worker;Password=worker";
        SuperuserConnection = $"{baseConnection};Username=postgres;Password=postgres";
        PublicationConnection = $"{baseConnection};Username=course_publication;Password=publication";
    }

    public Task DisposeAsync() => _postgres.StopAsync();
}