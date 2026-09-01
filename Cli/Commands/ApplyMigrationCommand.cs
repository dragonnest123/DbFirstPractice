using Shared.Utils;

namespace Cli.Commands;

public sealed class ApplyMigrationCommand : ICommand
{
    public string Name => "apply";
    public string Usage => "migration apply <directory>";

    public async Task<int> RunAsync(string[] args, CommandContext ctx)
    {
        if (args.Length != 1)
            return ctx.Envelope.Error("request.invalid", $"usage: {Usage}");

        var directory = args[0];
        if (!Directory.Exists(directory))
            return ctx.Envelope.Error("request.invalid", $"migration directory not found: {directory}");

        var files = Directory.GetFiles(directory, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            return ctx.Envelope.Error("request.invalid", "no migration files found");

        var applied = new List<string>();
        var skipped = new List<string>();

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            try
            {
                var sql = await File.ReadAllTextAsync(file);
                var checksum = HashUtil.Sha256Hex(sql);
                var existing = await ctx.Migrations.GetMigrationChecksumAsync(filename);
                
                if (existing is not null)
                {
                    if (existing == checksum)
                    {
                        skipped.Add(filename);
                        continue;
                    }
                    return ctx.Envelope.Error("manifest.conflict", $"migration file changed after apply: {filename}");
                }
                
                await ctx.Migrations.ApplyMigrationAsync(filename, checksum, sql);
                applied.Add(filename);
            }
            catch (Exception ex)
            {
                return ctx.Envelope.Error("migration.failed", $"failed to apply {filename}: {ex.Message}");
            }
        }

        return ctx.Envelope.Ok(new { resource = "migration", operation = "applied", applied, skipped });
    }
}