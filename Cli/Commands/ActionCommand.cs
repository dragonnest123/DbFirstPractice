using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public static class ActionCommand
{
    public static async Task<int> Handle(string[] args, CatalogService store, Envelope envelope)
    {
        if (args.Length == 0)
            return envelope.Error("request.invalid", "missing action subcommand");

        return args[0] switch
        {
            "validate" when args.Length >= 2 => await ValidateActionCommand.Handle(args[1], envelope),
            "publish" when args.Length >= 2 => await PublishActionCommand.Handle(args[1], store, envelope),
            "list" => await ListActionCommand.Handle(store, envelope),
            "activate" => await ActivateActionCommand.Handle(args[1..], store, envelope),
            "disable" => await DisableActionCommand.Handle(args[1..], store, envelope),
            _ => envelope.Error("request.invalid", $"unknown action subcommand: {string.Join(' ', args)}")
        };
    }
}