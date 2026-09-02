namespace Cli.Commands;

public interface ICommand
{
    string Name { get; }
    string Usage { get; }
    Task<int> RunAsync(string[] args);
}