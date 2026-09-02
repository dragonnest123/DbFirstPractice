using System.Text.Json;
using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public sealed class FlowSignalCommand(Envelope _envelope, FlowService _flows) : ICommand
{
    public string Name => "signal";
    public string Usage => "flow signal <process-id> --type <type> --message-id <id> --payload <file>";

    public async Task<int> RunAsync(string[] args)
    {
        if (!ArgParser.TryParse(args, out var positionals, out var flags)
            || positionals.Length != 1
            || !flags.TryGetValue("--type", out var type)
            || !flags.TryGetValue("--message-id", out var messageId)
            || !flags.TryGetValue("--payload", out var payloadPath)
            || !File.Exists(payloadPath))
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        try
        {
            var payload = await File.ReadAllTextAsync(payloadPath);
            var result = await _flows.SignalAsync(positionals[0], type, messageId, payload);
            using var doc = JsonDocument.Parse(result);
            var root = doc.RootElement;

            return _envelope.Ok(new
            {
                resource = "signal",
                processId = root.GetProperty("processId").GetString(),
                messageId = root.GetProperty("messageId").GetString(),
                signalType = root.GetProperty("signalType").GetString(),
                status = root.GetProperty("status").GetString()
            });
        }
        catch (PublicationException ex)
        {
            return _envelope.Error(ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return _envelope.Error("signal.failed", ex.Message);
        }
    }
}