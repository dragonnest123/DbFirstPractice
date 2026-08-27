using System.Text.RegularExpressions;

namespace Shared.Utils;

public static partial class IdentifierUtil
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled)]
    private static partial Regex SqlIdentifier();

    public static bool IsValidSqlIdentifier(string value)
        => SqlIdentifier().IsMatch(value);

    public static bool TryParseRoute(string route, out string module, out string action)
    {
        module = "";
        action = "";
        var parts = route.Split('.');
        if (parts.Length != 2) 
            return false;
        
        module = parts[0];
        action = parts[1];
        
        return IsValidSqlIdentifier(module) && IsValidSqlIdentifier(action);
    }
}
