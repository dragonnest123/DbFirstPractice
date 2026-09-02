using Cli.Services;
using Cli.Utils;
using Shared.Utils;

namespace Cli.Commands;

public sealed class ActivateActionCommand(Envelope _envelope, PublicationService _publication) : ICommand
{
    public string Name => "activate";
    public string Usage => "action activate <module.action> --version <version>";

    public async Task<int> RunAsync(string[] args)
    {
        if (!ArgParser.TryParse(args, out var positionals, out var flags)
            || positionals.Length != 1
            || !flags.TryGetValue("--version", out var rawVersion)
            || !int.TryParse(rawVersion, out var version) || version < 1)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        if (!IdentifierUtil.TryParseRoute(positionals[0], out var module, out var action))
            return _envelope.Error("request.invalid", "invalid route, expected <module>.<action>");

        try
        {
            await _publication.ActivateAsync(module, action, version);

            return _envelope.Ok(new { resource = "action", operation = "activated", key = $"{module}.{action}", version });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("action.activate_failed", ex.Message);
        }
    }
}