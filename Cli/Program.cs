namespace Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var envelope = new CliEnvelope();
        if (args.Length == 0)
            return envelope.Error("request.invalid", "missing command");

        try
        {
            return args[0] switch
            {
                "migration" when args.Length >= 3 && args[1] == "apply" => await MigrationCommand.ApplyAsync(args[2], envelope),
                "action" => await ActionCommand.RunAsync(args[1..], envelope),
                _ => envelope.Error("request.invalid", $"unknown command: {string.Join(' ', args)}")
            };
        }
        catch (Exception ex)
        {
            return envelope.Error("internal.error", ex.Message);
        }
    }
}