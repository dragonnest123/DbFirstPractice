using Cli.Utils;
using Shared.Utils;

namespace Cli.Commands;

public sealed class DisableActionCommand : ICommand
{
    public string Name => "disable";
    public string Usage => "action disable <module.action> --version <version> [--replacement-version <version>]";

    public async Task<int> RunAsync(string[] args, CommandContext ctx)
    {
        if (!ArgParser.TryParse(args, out var positionals, out var flags)
            || positionals.Length != 1
            || !flags.TryGetValue("--version", out var rawVersion)
            || !int.TryParse(rawVersion, out var version) || version < 1)
            return ctx.Envelope.Error("request.invalid", $"usage: {Usage}");

        if (!IdentifierUtil.TryParseRoute(positionals[0], out var module, out var action))
            return ctx.Envelope.Error("request.invalid", "invalid route, expected <module>.<action>");

        int? replacement = null;
        if (flags.TryGetValue("--replacement-version", out var rawReplacement))
        {
            if (!int.TryParse(rawReplacement, out var r) || r < 1)
                return ctx.Envelope.Error("request.invalid", "invalid replacement version");
            
            replacement = r;
        }

        try
        {
            var route = await ctx.Store.GetRouteAsync(module, action);
            var target = route.FirstOrDefault(m => m.Version == version);
            if (target is null)
                return ctx.Envelope.Error("action.not_found", $"version {version} of {module}.{action} is not published");

            var remaining = route.Where(m => m.Enabled && m.Version != version).ToList();
            if (replacement is null)
            {
                if (target.IsDefault || remaining.Count == 0)
                {
                    if (remaining.Count == 0)
                        return ctx.Envelope.Error("action.invalid", "replacement version required when route has no other enabled version");
                    replacement = remaining.OrderBy(m => m.Version).First().Version;
                }
            }
            else
            {
                var repl = remaining.FirstOrDefault(m => m.Version == replacement.Value);
                if (repl is null)
                    return ctx.Envelope.Error("action.invalid", "replacement version not found or disabled");
            }

            await ctx.Store.DisableAsync(module, action, version, replacement);
            
            return ctx.Envelope.Ok(new
            {
                resource = "action", operation = "disabled", key = $"{module}.{action}", version, replacementVersion = replacement
            });
        }
        catch (Exception ex)
        {
            return ctx.Envelope.Error("action.disable_failed", ex.Message);
        }
    }
}