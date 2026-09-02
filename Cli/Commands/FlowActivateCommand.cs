using System.Text.Json;
using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public sealed class FlowActivateCommand(Envelope _envelope, FlowService _flows) : ICommand
{
    public string Name => "activate";
    public string Usage => "flow activate <flow> --version <version>";

    public async Task<int> RunAsync(string[] args)
    {
        if (!ArgParser.TryParse(args, out var positionals, out var flags)
            || positionals.Length != 1
            || !flags.TryGetValue("--version", out var rawVersion)
            || !int.TryParse(rawVersion, out var version) || version < 1)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var result = await _flows.ActivateAsync(positionals[0], version);
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            return _envelope.Ok(new
            {
                resource = "flow",
                operation = "activated",
                flowName = root.GetProperty("flowName").GetString(),
                flowVersion = root.GetProperty("flowVersion").GetInt32()
            });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("flow.activate_failed", ex.Message);
        }
    }
}