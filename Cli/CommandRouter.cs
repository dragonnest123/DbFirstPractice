using Cli.Commands;
using Cli.Utils;

namespace Cli;

public sealed class CommandRouter : ICommand
{
    private readonly Envelope _envelope;
    private readonly Dictionary<string, ICommand> _commands;

    public CommandRouter(string name, string usage, Envelope envelope, IEnumerable<ICommand> commands)
    {
        Name = name;
        Usage = usage;
        _envelope = envelope;
        _commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    public string Name { get; }
    public string Usage { get; }

    public Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
            return Task.FromResult(_envelope.Error("request.invalid", $"missing {Name} subcommand"));

        if (!_commands.TryGetValue(args[0], out var command))
            return Task.FromResult(
                _envelope.Error("request.invalid", $"unknown {Name} subcommand: {string.Join(' ', args)}"));

        return command.RunAsync(args[1..]);
    }
}