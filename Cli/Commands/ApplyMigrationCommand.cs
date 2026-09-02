using Cli.Services;
using Cli.Utils;
using Shared.Utils;

namespace Cli.Commands;

public sealed class ApplyMigrationCommand(Envelope _envelope, MigrationService _migrations) : ICommand
{
    public string Name => "apply";
    public string Usage => "migration apply <directory>";

    public async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 1)
            return _envelope.Error("request.invalid", $"usage: {Usage}");

        var directory = args[0];
        if (!Directory.Exists(directory))
            return _envelope.Error("request.invalid", $"migration directory not found: {directory}");

        var files = Directory.GetFiles(directory, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            return _envelope.Error("request.invalid", "no migration files found");

        var applied = new List<string>();
        var skipped = new List<string>();

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            try
            {
                var sql = await File.ReadAllTextAsync(file);
                var checksum = HashUtil.Sha256Hex(sql);
                var existing = await _migrations.GetMigrationChecksumAsync(filename);
                
                if (existing is not null)
                {
                    if (existing == checksum)
                    {
                        skipped.Add(filename);
                        continue;
                    }
                    return _envelope.Error("manifest.conflict", $"migration file changed after apply: {filename}");
                }
                
                await _migrations.ApplyMigrationAsync(filename, checksum, sql);
                applied.Add(filename);
            }
            catch (Exception ex)
            {
                return _envelope.Error("migration.failed", $"failed to apply {filename}: {ex.Message}");
            }
        }

        return _envelope.Ok(new { resource = "migration", operation = "applied", applied, skipped });
    }
}