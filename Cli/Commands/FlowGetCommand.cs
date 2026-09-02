using System.Text.Json;
using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public sealed class FlowGetCommand(Envelope _envelope, FlowService _flows) : ICommand
{
    public string Name => "get";
    public string Usage => "flow get <process-id>";

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var result = await _flows.GetAsync(args[0]);
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;
            var process = root.GetProperty("process");

            return _envelope.Ok(new
            {
                resource = "process",
                processId = process.GetProperty("processId").GetString(),
                flowName = process.GetProperty("flowName").GetString(),
                flowVersion = process.GetProperty("flowVersion").GetInt32(),
                state = process.GetProperty("state").GetString(),
                currentStepKey = process.TryGetProperty("currentStepKey", out var step)
                    ? step.GetString()
                    : null
            });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("process.get_failed", ex.Message);
        }
    }
}