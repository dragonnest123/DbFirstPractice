using Cli.Services;
using Cli.Utils;
using Shared.Models;

namespace Cli.Commands;

public sealed class PublishActionCommand(Envelope _envelope, PublicationService _publication) : ICommand
{
    public string Name => "publish";
    public string Usage => "action publish <manifest.json>";

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        var path = args[0];
        if (!File.Exists(path))
            return _envelope.Error("manifest.notfound", $"manifest file not found: {path}");
        if (!ManifestValidator.IsValid(path))
            return _envelope.Error("manifest.invalid", "manifest does not match schema");
        if (!ActionManifest.TryLoad(path, out var manifest, out var error))
            return _envelope.Error("manifest.invalid", error ?? "cannot read manifest");

        try
        {
            await _publication.PublishAsync(await File.ReadAllTextAsync(path));
            return _envelope.Ok(new { resource = "action", operation = "published", key = manifest!.Key, version = manifest.Version });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("manifest.publish_failed", ex.Message);
        }
    }
}