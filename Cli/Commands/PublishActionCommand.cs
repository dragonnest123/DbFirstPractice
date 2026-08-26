using Cli.Models;
using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public static class PublishActionCommand
{
    public static async Task<int> Handle(string path, CatalogService store, Envelope envelope)
    {
        if (!ManifestValidator.IsValid(path))
            return envelope.Error("manifest.invalid", "manifest does not match schema");
        if (!Manifest.TryLoad(path, out var manifest, out var error))
            return envelope.Error("manifest.invalid", error ?? "cannot read manifest");

        try
        {
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
}