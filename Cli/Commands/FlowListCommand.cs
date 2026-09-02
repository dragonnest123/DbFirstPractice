using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public sealed class FlowListCommand(Envelope _envelope, FlowService _flows) : ICommand
{
    public string Name => "list";
    public string Usage => "flow list";

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 0)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var items = await _flows.ListAsync();
            return _envelope.Ok(new { items });
        }
        catch (Exception ex)
        {
            return _envelope.Error("flow.list_failed", ex.Message);
        }
    }
}