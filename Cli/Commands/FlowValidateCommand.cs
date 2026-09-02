using Cli.Utils;
using Shared.Services;

namespace Cli.Commands;

public sealed class FlowValidateCommand(Envelope _envelope, ActionCatalogService _store) : ICommand
{
    public string Name => "validate";
    public string Usage => "flow validate <map.json|map.yaml>";

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var node = FlowMapLoader.Load(args[0]);
            if (node is null)
                return _envelope.Error("map.invalid", "cannot read map file");

            var schemaError = FlowMapValidator.ValidateSchema(node);
            if (schemaError is not null)
                return _envelope.Error("map.invalid", schemaError);

            var semanticError = await FlowMapValidator.ValidateSemanticsAsync(node, _store);
            if (semanticError is not null)
                return _envelope.Error("flow.invalid", semanticError);

            return _envelope.Ok(new { resource = "flow", operation = "validated" });
        }
        catch (Exception ex)
        {
            return _envelope.Error("map.invalid", ex.Message);
        }
    }
}