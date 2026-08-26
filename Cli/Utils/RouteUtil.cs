using System.Text.RegularExpressions;

namespace Cli.Utils;

public static class RouteUtil
{
    private static readonly Regex SqlIdentifier = new(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled);

    public static bool TryParseRoute(string route, out string module, out string action)
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