using Cli.Utils;
using Shared.Services;

namespace Cli.Commands;

public sealed class ListActionCommand(Envelope _envelope, ActionCatalogService _store) : ICommand
{
    public string Name => "list";
    public string Usage => "action list";

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 0)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var items = await _store.ListAllAsync();
            return _envelope.Ok(new
            {
                items = items.Select(i => new { module = i.Module, action = i.Action, version = i.Version }).ToArray()
            });
        }
        catch (Exception ex)
        {
            return _envelope.Error("action.list_failed", ex.Message);
        }
    }
}