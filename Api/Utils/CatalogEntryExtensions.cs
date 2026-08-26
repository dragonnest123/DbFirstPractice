using System.Text.Json;
using Api.Services;

namespace Api.Utils;

public static class CatalogEntryExtensions
{
    public static bool HasRequiredPolicy(this CatalogEntry entry, string[] scopes)
        => entry.RequiredPolicy.EnumerateArray().Select(x => x.GetString()!).All(need => scopes.Contains(need));
}