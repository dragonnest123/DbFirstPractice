using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public static class ActivateActionCommand
{
    public static async Task<int> Handle(string[] args, CatalogService store, Envelope envelope)
    {
        if (args.Length < 3 || args[1] != "--version")
            return envelope.Error("request.invalid", "usage: action activate <module.action> --version <version>");
        if (!RouteUtil.TryParseRoute(args[0], out var module, out var action))
            return envelope.Error("request.invalid", "invalid route, expected <module>.<action>");
        if (!int.TryParse(args[2], out var version) || version < 1)
            return envelope.Error("request.invalid", "invalid version");

        try
        {
            var existing = await store.FindManifestAsync(module, action, version);
            if (existing is null)
                return envelope.Error("action.not_found", $"version {version} of {module}.{action} is not published");
            await store.ActivateAsync(module, action, version);
            return envelope.Ok(new { resource = "action", operation = "activated", key = $"{module}.{action}", version });
        }
        catch (Exception ex)
        {
            return envelope.Error("action.activate_failed", ex.Message);
        }
    }
}