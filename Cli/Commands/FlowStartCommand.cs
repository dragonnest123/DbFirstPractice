using System.Text.Json;
using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public sealed class FlowStartCommand(Envelope _envelope, FlowService _flows) : ICommand
{
    public string Name => "start";
    public string Usage => "flow start <flow> --business-key <key> [--data <file>]";

    public async Task<int> RunAsync(string[] args)
    {
        if (!ArgParser.TryParse(args, out var positionals, out var flags)
            || positionals.Length != 1
            || !flags.TryGetValue("--business-key", out var businessKey)
            || string.IsNullOrWhiteSpace(businessKey))
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        var data = "{}";
        if (flags.TryGetValue("--data", out var dataPath))
        {
            if (!File.Exists(dataPath))
                return _envelope.Error("process.data_not_found", $"data file not found: {dataPath}");
            data = await File.ReadAllTextAsync(dataPath);
        }

        try
        {
            var result = await _flows.StartAsync(positionals[0], businessKey, data);
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            return _envelope.Ok(new
            {
                resource = "process",
                operation = "started",
                processId = root.GetProperty("processId").GetString(),
                flowName = root.GetProperty("flowName").GetString(),
                flowVersion = root.GetProperty("flowVersion").GetInt32(),
                state = root.GetProperty("state").GetString()
            });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("process.start_failed", ex.Message);
        }
    }
}