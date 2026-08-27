using System.Text.Json;
using Shared;

namespace Cli.Utils;

public sealed class Envelope
{
    private const string ContractVersion = Contract.ContractVersion;

    public int Ok(object? result = null)
    {
        var body = new { status = "ok", result, meta = new { contractVersion = ContractVersion } };
        Console.Out.WriteLine(JsonSerializer.Serialize(body));
        
        return 0;
    }

    public int Error(string code, string message)
    {
        Console.Error.WriteLine(message);
        
        var body = new { status = "error", code, message, meta = new { contractVersion = ContractVersion } };
        Console.Out.WriteLine(JsonSerializer.Serialize(body));
        
        return 1;
    }
}