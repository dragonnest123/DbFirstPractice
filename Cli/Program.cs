using Cli.Commands;
using Cli.Services;
using Cli.Utils;

namespace Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var envelope = new Envelope();
        if (args.Length == 0)
            return envelope.Error("request.invalid", "missing command");

        try
        {
            var catalog = new CatalogService();
            var migrations = new MigrationService();
            return args[0] switch
            {
                "migration" when args.Length >= 3 && args[1] == "apply" => await MigrationCommand.Handle(args[2], migrations, envelope),
                "action" => await ActionCommand.Handle(args[1..], catalog, envelope),
                _ => envelope.Error("request.invalid", $"unknown command: {string.Join(' ', args)}")
            };
        }
        catch (Exception ex)
        {
            return envelope.Error("internal.error", ex.Message);
        }
    }
}