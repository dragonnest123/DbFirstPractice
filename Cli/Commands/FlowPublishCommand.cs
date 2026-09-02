using System.Text.Json;
using Cli.Services;
using Cli.Utils;
using Shared.Services;

namespace Cli.Commands;

public sealed class FlowPublishCommand(
    Envelope _envelope,
    ActionCatalogService _store,
    FlowService _flows) : ICommand
{
    public string Name => "publish";
    public string Usage => "flow publish <map.json|map.yaml>";

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

            var result = await _flows.PublishAsync(node.ToJsonString());
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            return _envelope.Ok(new
            {
                resource = "flow",
                operation = "published",
                flowName = root.GetProperty("flowName").GetString(),
                flowVersion = root.GetProperty("flowVersion").GetInt32()
            });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("map.publish_failed", ex.Message);
        }
    }
}