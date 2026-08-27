using Cli.Utils;
using Shared.Models;

namespace Cli.Commands;

public sealed class ValidateActionCommand : ICommand
{
    public string Name => "validate";
    public string Usage => "action validate <manifest.json>";

    public Task<int> RunAsync(string[] args, CommandContext ctx)
    {
        if (args.Length != 1)
            return Task.FromResult(ctx.Envelope.Error("request.invalid", $"usage: {Usage}"));

        if (!ManifestValidator.IsValid(args[0]))
            return Task.FromResult(ctx.Envelope.Error("manifest.invalid", "manifest does not match schema"));
        if (!ActionManifest.TryLoad(args[0], out var manifest, out var error))
            return Task.FromResult(ctx.Envelope.Error("manifest.invalid", error ?? "cannot read manifest"));

        return Task.FromResult(ctx.Envelope.Ok(
            new { resource = "action", operation = "validated", key = manifest!.Key, version = manifest.Version }));
    }
}