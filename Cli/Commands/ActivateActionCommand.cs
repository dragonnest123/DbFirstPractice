using Cli.Services;
using Cli.Utils;
using Shared.Utils;

namespace Cli.Commands;

public sealed class ActivateActionCommand : ICommand
{
    public string Name => "activate";
    public string Usage => "action activate <module.action> --version <version>";

    public async Task<int> RunAsync(string[] args, CommandContext ctx)
    {
        if (!ArgParser.TryParse(args, out var positionals, out var flags)
            || positionals.Length != 1
            || !flags.TryGetValue("--version", out var rawVersion)
            || !int.TryParse(rawVersion, out var version) || version < 1)
            return ctx.Envelope.Error("request.invalid", $"usage: {Usage}");

        if (!IdentifierUtil.TryParseRoute(positionals[0], out var module, out var action))
            return ctx.Envelope.Error("request.invalid", "invalid route, expected <module>.<action>");

        try
        {
            await ctx.Publication.ActivateAsync(module, action, version);

            return ctx.Envelope.Ok(new { resource = "action", operation = "activated", key = $"{module}.{action}", version });
        }
        catch (PublicationException ex)
        {
            return ctx.Envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return ctx.Envelope.Error("action.activate_failed", ex.Message);
        }
    }
}