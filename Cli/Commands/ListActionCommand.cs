using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public static class ListActionCommand
{
    public static async Task<int> Handle(CatalogService store, Envelope envelope)
    {
        try
        {
            var items = await store.ListAllAsync();
            return envelope.Ok(new { items = items.Select(i => new { module = i.Module, action = i.Action, version = i.Version }).ToArray() });
        }
        catch (Exception ex)
        {
            return envelope.Error("action.list_failed", ex.Message);
        }
    }
}