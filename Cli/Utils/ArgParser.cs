namespace Cli.Utils;

public static class ArgParser
{
    public static bool TryParse(string[] args, out string[] positionals, out Dictionary<string, string> flags)
    {
        var positional = new List<string>();
        var flagMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(arg);
                continue;
            }
            if (i + 1 >= args.Length)
            {
                positionals = Array.Empty<string>();
                flags = new Dictionary<string, string>();
                return false;
            }
            flagMap[arg] = args[++i];
        }
        positionals = positional.ToArray();
        flags = flagMap;
        return true;
    }
}