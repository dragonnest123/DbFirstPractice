using Cli.Models;
using Cli.Utils;

namespace Cli.Commands;

public static class ValidateActionCommand
{
    public static async Task<int> Handle(string path, Envelope envelope)
    {
        if (!ManifestValidator.IsValid(path))
            return envelope.Error("manifest.invalid", "manifest does not match schema");
        if (!Manifest.TryLoad(path, out var manifest, out var error))
            return envelope.Error("manifest.invalid", error ?? "cannot read manifest");
        return envelope.Ok(new { resource = "action", operation = "validated", key = manifest!.Key, version = manifest.Version });
    }
}