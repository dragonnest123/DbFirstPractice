using Cli.Commands;
using Cli.Services;
using Cli.Utils;
using Shared.Services;

namespace Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var ctx = new CommandContext
        {
            Envelope = new Envelope(),
            Store = new ActionCatalogService(
                Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
                ?? "Host=postgres;Port=5432;Database=course;Username=course_migration;Password=migration;Include Error Detail=false"),
            Migrations = new MigrationService()
        };

        var router = new CommandRouter("cli", "cli <action|migration> ...", [
            new CommandRouter("action", "action <validate|publish|list|activate|disable> ...", [
                new ValidateActionCommand(),
                new PublishActionCommand(),
                new ListActionCommand(),
                new ActivateActionCommand(),
                new DisableActionCommand()
            ]),
            new CommandRouter("migration", "migration apply <directory>", [
                new ApplyMigrationCommand()
            ])
        ]);

        try
        {
            return await router.RunAsync(args, ctx);
        }
        catch (Exception ex)
        {
            return ctx.Envelope.Error("internal.error", ex.Message);
        }
    }
}