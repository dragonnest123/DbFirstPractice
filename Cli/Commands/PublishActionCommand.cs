using Cli.Utils;
using Shared.Models;

namespace Cli.Commands;

public sealed class PublishActionCommand : ICommand
{
    public string Name => "publish";
    public string Usage => "action publish <manifest.json>";

    public async Task<int> RunAsync(string[] args, CommandContext ctx)
    {
        if (args.Length != 1)
            return ctx.Envelope.Error("request.invalid", $"usage: {Usage}");

        var path = args[0];
        if (!File.Exists(path))
            return ctx.Envelope.Error("manifest.notfound", $"manifest file not found: {path}");
        if (!ManifestValidator.IsValid(path))
            return ctx.Envelope.Error("manifest.invalid", "manifest does not match schema");
        if (!ActionManifest.TryLoad(path, out var manifest, out var error))
            return ctx.Envelope.Error("manifest.invalid", error ?? "cannot read manifest");

        try
        {
            var existing = await ctx.Store.FindManifestAsync(manifest!.Module, manifest.Action, manifest.Version);
            if (existing is not null)
            {
                if (existing.SameAs(manifest))
                    return ctx.Envelope.Ok(new { resource = "action", operation = "published", key = manifest.Key, version = manifest.Version });
                return ctx.Envelope.Error("manifest.conflict", "published action version is immutable");
            }

            if (manifest.IsDefault && await ctx.Store.HasDefaultAsync(manifest.Module, manifest.Action))
                return ctx.Envelope.Error("manifest.conflict", "route already has a default version");

            await ctx.Store.InsertManifestAsync(manifest);
            return ctx.Envelope.Ok(new { resource = "action", operation = "published", key = manifest.Key, version = manifest.Version });
        }
        catch (Exception ex)
        {
            return ctx.Envelope.Error("manifest.publish_failed", ex.Message);
        }
    }
}