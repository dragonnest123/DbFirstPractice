using System.Text.RegularExpressions;

namespace Cli;

public static class ActionCommand
{
    private static readonly Regex SqlIdentifier = new(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    public static async Task<int> RunAsync(string[] args, CliEnvelope envelope)
    {
        if (args.Length == 0)
            return envelope.Error("request.invalid", "missing action subcommand");

        return args[0] switch
        {
            "validate" when args.Length >= 2 => await ValidateAsync(args[1], envelope),
            "publish" when args.Length >= 2 => await PublishAsync(args[1], envelope),
            "list" => await ListAsync(envelope),
            "activate" => await ActivateAsync(args[1..], envelope),
            "disable" => await DisableAsync(args[1..], envelope),
            _ => envelope.Error("request.invalid", $"unknown action subcommand: {string.Join(' ', args)}")
        };
    }

    private static async Task<int> ValidateAsync(string path, CliEnvelope envelope)
    {
        if (!ManifestValidator.IsValid(path))
            return envelope.Error("manifest.invalid", "manifest does not match schema");
        if (!Manifest.TryLoad(path, out var manifest, out var error))
            return envelope.Error("manifest.invalid", error ?? "cannot read manifest");
        return envelope.Ok(new { resource = "action", operation = "validated", key = manifest!.Key, version = manifest.Version });
    }

    private static async Task<int> PublishAsync(string path, CliEnvelope envelope)
    {
        if (!ManifestValidator.IsValid(path))
            return envelope.Error("manifest.invalid", "manifest does not match schema");
        if (!Manifest.TryLoad(path, out var manifest, out var error))
            return envelope.Error("manifest.invalid", error ?? "cannot read manifest");

        try
        {
            var store = new CatalogStore();
            var existing = await store.FindManifestAsync(manifest!.Module, manifest.Action, manifest.Version);
            if (existing is not null)
            {
                if (existing.SameAs(manifest))
                    return envelope.Ok(new { resource = "action", operation = "published", key = manifest.Key, version = manifest.Version });
                return envelope.Error("manifest.conflict", "published action version is immutable");
            }

            if (manifest.IsDefault && await store.HasDefaultAsync(manifest.Module, manifest.Action))
                return envelope.Error("manifest.conflict", "route already has a default version");

            await store.InsertManifestAsync(manifest);
            return envelope.Ok(new { resource = "action", operation = "published", key = manifest.Key, version = manifest.Version });
        }
        catch (Exception ex)
        {
            return envelope.Error("manifest.publish_failed", ex.Message);
        }
    }

    private static async Task<int> ListAsync(CliEnvelope envelope)
    {
        try
        {
            var store = new CatalogStore();
            var items = await store.ListAllAsync();
            return envelope.Ok(new { items = items.Select(i => new { module = i.Module, action = i.Action, version = i.Version }).ToArray() });
        }
        catch (Exception ex)
        {
            return envelope.Error("action.list_failed", ex.Message);
        }
    }

    private static async Task<int> ActivateAsync(string[] args, CliEnvelope envelope)
    {
        if (args.Length < 3 || args[1] != "--version")
            return envelope.Error("request.invalid", "usage: action activate <module.action> --version <version>");
        if (!TryParseRoute(args[0], out var module, out var action))
            return envelope.Error("request.invalid", "invalid route, expected <module>.<action>");
        if (!int.TryParse(args[2], out var version) || version < 1)
            return envelope.Error("request.invalid", "invalid version");

        try
        {
            var store = new CatalogStore();
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

    private static async Task<int> DisableAsync(string[] args, CliEnvelope envelope)
    {
        if (args.Length < 3 || args[1] != "--version")
            return envelope.Error("request.invalid", "usage: action disable <module.action> --version <version> [--replacement-version <version>]");
        if (!TryParseRoute(args[0], out var module, out var action))
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
            var store = new CatalogStore();
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

    private static bool TryParseRoute(string route, out string module, out string action)
    {
        module = "";
        action = "";
        var parts = route.Split('.');
        if (parts.Length != 2) return false;
        module = parts[0];
        action = parts[1];
        return SqlIdentifier.IsMatch(module) && SqlIdentifier.IsMatch(action);
    }
}