using System.Diagnostics;
using Xunit;

namespace Api.Tests;

/// <summary>
/// The week-4 entry points have to be runnable straight from a fresh clone. Both
/// properties were silent failures: a core.fileMode=false checkout strips the
/// executable bit from the shell scripts, and a socket-only Postgres healthcheck
/// turns green before the server accepts TCP, so the CLI starts too early.
/// </summary>
public class RepositoryStartupContractTests
{
    private static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "compose.yaml"))
                && Directory.Exists(Path.Combine(dir.FullName, "Api", "Migrations")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("repository root not found");
    }

    [Theory]
    [InlineData("task/week4/autocheck/check.sh")]
    [InlineData("task/week4/autocheck/safe_compose.sh")]
    [InlineData("task/week4/check.sh")]
    public void WeekFourScriptsAreExecutableInGitIndex(string path)
    {
        var mode = GitMode(path);

        Assert.NotNull(mode);
        Assert.Equal("100755", mode);
    }

    [Fact]
    public void PostgresHealthcheckProbesTcp()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot, "compose.yaml"));

        Assert.Contains("pg_isready -h 127.0.0.1 -p 5432", compose);
    }

    [Fact]
    public void CliWaitsForPostgresToReportHealthy()
    {
        var compose = File.ReadAllText(Path.Combine(RepoRoot, "compose.yaml"));
        var cli = compose[compose.IndexOf("\n  cli:", StringComparison.Ordinal)
            ..compose.IndexOf("\n  worker-a:", StringComparison.Ordinal)];

        Assert.Contains("postgres:", cli);
        Assert.Contains("condition: service_healthy", cli);
    }

    private static string? GitMode(string path)
    {
        var start = new ProcessStartInfo("git", $"ls-files -s -- \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot
        };

        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);
            if (process.ExitCode != 0)
                return null;

            var line = output.Trim();
            return line.Length == 0 ? null : line.Split(' ')[0];
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No git on this host: nothing to assert about the index.
            return null;
        }
    }
}