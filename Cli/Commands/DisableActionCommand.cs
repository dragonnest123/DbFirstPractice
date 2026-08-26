using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public static class DisableActionCommand
{
    public static async Task<int> Handle(string[] args, CatalogService store, Envelope envelope)
    {
        if (args.Length < 3 || args[1] != "--version")
            return envelope.Error("request.invalid", "usage: action disable <module.action> --version <version> [--replacement-version <version>]");
        if (!RouteUtil.TryParseRoute(args[0], out var module, out var action))
            return envelope.Error("request.invalid", "invalid route, expected <module>.<action>");
        if (!int.TryParse(args[2], out var version) || version < 1)
            return envelope.Error("request.invalid", "invalid version");

        int? replacement = null;
        if (args.Length >= 5 && args[3] == "--replacement-version")
        {
            if (!int.TryParse(args[4], out var r) || r < 1)
                return envelope.Error("request.invalid", "invalid replacement version");
            replacement = r;
        }

        try
        {
            var route = await store.GetRouteAsync(module, action);
            var target = route.FirstOrDefault(m => m.Version == version);
            if (target is null)
                return envelope.Error("action.not_found", $"version {version} of {module}.{action} is not published");

            var remaining = route.Where(m => m.Enabled && m.Version != version).ToList();
            if (replacement is null)
            {
                if (target.IsDefault || remaining.Count == 0)
                {
                    if (remaining.Count == 0)
                        return envelope.Error("action.invalid", "replacement version required when route has no other enabled version");
                    replacement = remaining.OrderBy(m => m.Version).First().Version;
                }
            }
            else
            {
                var repl = remaining.FirstOrDefault(m => m.Version == replacement.Value);
                if (repl is null)
                    return envelope.Error("action.invalid", "replacement version not found or disabled");
            }

            await store.DisableAsync(module, action, version, replacement);
            return envelope.Ok(new { resource = "action", operation = "disabled", key = $"{module}.{action}", version, replacementVersion = replacement });
        }
        catch (Exception ex)
        {
            return envelope.Error("action.disable_failed", ex.Message);
        }
    }
}