using Cli.Commands;

namespace Cli;

public sealed class CommandRouter : ICommand
{
    private readonly Dictionary<string, ICommand> _commands;

    public CommandRouter(string name, string usage, IEnumerable<ICommand> commands)
    {
        Name = name;
        Usage = usage;
        _commands = commands.ToDictionary(c => c.Name, StringComparer.Ordinal);
    }

    public string Name { get; }
    public string Usage { get; }

    public async Task<int> RunAsync(string[] args, CommandContext ctx)
    {
        if (args.Length == 0)
            return ctx.Envelope.Error("request.invalid", $"missing {Name} subcommand");

        if (!_commands.TryGetValue(args[0], out var command))
            return ctx.Envelope.Error("request.invalid", $"unknown {Name} subcommand: {string.Join(' ', args)}");

        return await command.RunAsync(args[1..], ctx);
    }
}