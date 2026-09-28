using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public sealed class ApplyDeliveryPolicyCommand(Envelope _envelope, DeliveryPolicyService _policy) : ICommand
{
    public string Name => "apply";
    public string Usage => "delivery policy apply";

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 0)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        var policy = DeliveryPolicyService.FromEnvironment();
        try
        {
            var result = await _policy.ApplyAsync(policy);
            if (result.TryGetProperty("status", out var status)
                && status.GetString() == "error")
            {
                var code = result.TryGetProperty("code", out var value) ? value.GetString() : "policy.invalid";
                var message = result.TryGetProperty("message", out var text) ? text.GetString() : "policy rejected";
                return _envelope.Error(code ?? "policy.invalid", message ?? "policy rejected");
            }

            return _envelope.Ok(new { resource = "delivery.policy", operation = "applied", result });
        }
        catch (Exception ex)
        {
            return _envelope.Error("policy.apply_failed", ex.Message);
        }
    }
}
