using Shared.Utils;

namespace Shared.Services;

/// <summary>
/// Single armed commit-boundary failpoint, honoured in the test profile only.
/// Reaching the boundary publishes one acknowledgement line and then parks the
/// process: the harness stops the container once it observes the line, so the
/// only durable state is whatever the boundary already committed.
/// </summary>
public sealed class FailpointController
{
    public const string TestProfileVariable = "COURSE_TEST_PROFILE";
    public const string Variable = "COURSE_FAILPOINT";

    private readonly string? _armed;
    private readonly bool _enabled;
    private int _announced;

    public FailpointController(string? armed, bool testProfile)
    {
        _armed = string.IsNullOrWhiteSpace(armed) ? null : armed.Trim();
        _enabled = testProfile && _armed is not null;
    }

    public static FailpointController FromEnvironment() =>
        new(
            Environment.GetEnvironmentVariable(Variable),
            Environment.GetEnvironmentVariable(TestProfileVariable) == "1");

    public string? ArmedName => _enabled ? _armed : null;

    public bool IsArmed(string name) => _enabled && _armed == name;

    public void Reach(string name, string instanceId)
    {
        if (!IsArmed(name) || Interlocked.Exchange(ref _announced, 1) == 1)
            return;

        StructuredLog.Write("failpoint.reached", new Dictionary<string, object?>
        {
            ["name"] = name,
            ["instanceId"] = instanceId
        });

        BlockForever();
    }

    public string Describe() => _armed is null ? "none" : _armed + (_enabled ? ":armed" : ":ignored");

    private static void BlockForever() => Thread.Sleep(Timeout.Infinite);
}
