using System.Text.Json;

namespace Cli;

public sealed class CliEnvelope
{
    private const string ContractVersion = "course-1";

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