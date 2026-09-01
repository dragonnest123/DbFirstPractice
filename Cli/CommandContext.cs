using Cli.Services;
using Cli.Utils;
using Shared.Services;

namespace Cli;

public sealed class CommandContext
{
    public required Envelope Envelope { get; init; }
    public required ActionCatalogService Store { get; init; }
    public required PublicationService Publication { get; init; }
    public required MigrationService Migrations { get; init; }
}