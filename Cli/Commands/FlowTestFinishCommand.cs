using System.Text.Json;
using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public sealed class FlowTestFinishCommand(Envelope _envelope, FlowService _flows) : ICommand
{
    public string Name => "test-finish";
    public string Usage => "flow test-finish <job-id> --owner <owner> --lease-version <version> --outcome <outcome> --result <file>";

    public async Task<int> RunAsync(string[] args)
    {
        if (Environment.GetEnvironmentVariable("COURSE_TEST_PROFILE") != "1")
            return _envelope.Error("request.invalid", "test-finish is only available with COURSE_TEST_PROFILE=1");

        if (!ArgParser.TryParse(args, out var positionals, out var flags)
            || positionals.Length != 1
            || !flags.TryGetValue("--owner", out var owner)
            || !flags.TryGetValue("--lease-version", out var rawLease)
            || !long.TryParse(rawLease, out var leaseVersion)
            || !flags.TryGetValue("--outcome", out var outcome)
            || !flags.TryGetValue("--result", out var resultPath)
            || !File.Exists(resultPath))
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var result = await File.ReadAllTextAsync(resultPath);
            var finish = await _flows.TestFinishAsync(positionals[0], owner, leaseVersion, outcome, result);
            using var doc = JsonDocument.Parse(finish);
            var root = doc.RootElement;

            return _envelope.Ok(new
            {
                resource = "process",
                operation = "finished",
                jobId = root.TryGetProperty("jobId", out var job) ? job.GetString() : positionals[0],
                processId = root.TryGetProperty("processId", out var process)
                    ? process.GetString()
                    : null,
                state = root.TryGetProperty("state", out var state) ? state.GetString() : null
            });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("process.finish_failed", ex.Message);
        }
    }
}