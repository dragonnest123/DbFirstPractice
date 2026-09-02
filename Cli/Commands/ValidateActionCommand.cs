using Cli.Utils;
using Shared.Models;

namespace Cli.Commands;

public sealed class ValidateActionCommand(Envelope _envelope) : ICommand
{
    public string Name => "validate";
    public string Usage => "action validate <manifest.json>";

    public Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
            return Task.FromResult(_envelope.Error("request.invalid", $"usage: {Usage}"));

        if (!File.Exists(args[0]))
            return Task.FromResult(_envelope.Error("manifest.notfound", $"manifest file not found: {args[0]}"));
        if (!ManifestValidator.IsValid(args[0]))
            return Task.FromResult(_envelope.Error("manifest.invalid", "manifest does not match schema"));
        if (!ActionManifest.TryLoad(args[0], out var manifest, out var error))
            return Task.FromResult(_envelope.Error("manifest.invalid", error ?? "cannot read manifest"));

        return Task.FromResult(_envelope.Ok(
            new { resource = "action", operation = "validated", key = manifest!.Key, version = manifest.Version }));
    }
}