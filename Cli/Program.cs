using Cli.Commands;
using Cli.Services;
using Cli.Utils;
using Shared.Services;

namespace Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var publicationConn = Environment.GetEnvironmentVariable("PUBLICATION_CONNECTION")
            ?? "Host=postgres;Port=5432;Database=course;Username=course_publication;Password=publication;Include Error Detail=false";

        var envelope = new Envelope();
        var store = new ActionCatalogService(publicationConn);
        var publication = new PublicationService(publicationConn);
        var flows = new FlowService(publicationConn);
        var migrations = new MigrationService();

        var router = new CommandRouter("cli", "cli <action|flow|migration> ...", envelope, [
            new CommandRouter("action", "action <validate|publish|list|activate|disable> ...", envelope, [
                new ValidateActionCommand(envelope),
                new PublishActionCommand(envelope, publication),
                new ListActionCommand(envelope, store),
                new ActivateActionCommand(envelope, publication),
                new DisableActionCommand(envelope, store, publication)
            ]),
            new CommandRouter("flow", "flow <validate|publish|list|activate|start|get|signal|test-finish> ...", envelope, [
                new FlowValidateCommand(envelope, store),
                new FlowPublishCommand(envelope, store, flows),
                new FlowListCommand(envelope, flows),
                new FlowActivateCommand(envelope, flows),
                new FlowStartCommand(envelope, flows),
                new FlowGetCommand(envelope, flows),
                new FlowSignalCommand(envelope, flows),
                new FlowTestFinishCommand(envelope, flows)
            ]),
            new CommandRouter("migration", "migration apply <directory>", envelope, [
                new ApplyMigrationCommand(envelope, migrations)
            ])
        ]);

        try
        {
            return await router.RunAsync(args);
        }
        catch (Exception ex)
        {
            return envelope.Error("internal.error", ex.Message);
        }
    }
}