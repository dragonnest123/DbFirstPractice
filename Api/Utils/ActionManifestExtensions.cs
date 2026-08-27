using Shared.Models;

namespace Api.Utils;

public static class ActionManifestExtensions
{
    public static bool HasRequiredPolicy(this ActionManifest entry, string[] scopes)
        => entry.RequiredPolicy.All(need => scopes.Contains(need));
}
