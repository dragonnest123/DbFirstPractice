namespace Cli.Commands;

public sealed class ListActionCommand : ICommand
{
    public string Name => "list";
    public string Usage => "action list";

    public async Task<int> RunAsync(string[] args, CommandContext ctx)
    {
        if (args.Length != 0)
            return ctx.Envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var items = await ctx.Store.ListAllAsync();
            return ctx.Envelope.Ok(new
            {
                items = items.Select(i => new { module = i.Module, action = i.Action, version = i.Version }).ToArray()
            });
        }
        catch (Exception ex)
        {
            return ctx.Envelope.Error("action.list_failed", ex.Message);
        }
    }
}