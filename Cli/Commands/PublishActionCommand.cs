using Cli.Services;
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
            await ctx.Publication.PublishAsync(await File.ReadAllTextAsync(path));
            return ctx.Envelope.Ok(new { resource = "action", operation = "published", key = manifest!.Key, version = manifest.Version });
        }
        catch (PublicationException ex)
        {
            return ctx.Envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return ctx.Envelope.Error("manifest.publish_failed", ex.Message);
        }
    }
}