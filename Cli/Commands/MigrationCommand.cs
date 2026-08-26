using Cli.Services;
using Cli.Utils;

namespace Cli.Commands;

public static class MigrationCommand
{
    public static async Task<int> Handle(string directory, MigrationService store, Envelope envelope)
    {
        if (!Directory.Exists(directory))
            return envelope.Error("request.invalid", $"migration directory not found: {directory}");

        var files = Directory.GetFiles(directory, "*.sql")
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (files.Length == 0)
            return envelope.Error("request.invalid", "no migration files found");

        var applied = new List<string>();
        var skipped = new List<string>();

        foreach (var file in files)
        {
            var filename = Path.GetFileName(file);
            try
            {
                var sql = await File.ReadAllTextAsync(file);
                var checksum = HashUtil.Sha256Hex(sql);
                var existing = await store.GetMigrationChecksumAsync(filename);
                if (existing is not null)
                {
                    if (existing == checksum)
                    {
                        skipped.Add(filename);
                        continue;
                    }
                    return envelope.Error("manifest.conflict", $"migration file changed after apply: {filename}");
                }
                await store.ApplyMigrationAsync(filename, checksum, sql);
                await store.GrantUsageToOwnerAsync();
                applied.Add(filename);
            }
            catch (Exception ex)
            {
                return envelope.Error("migration.failed", $"failed to apply {filename}: {ex.Message}");
            }
        }

        return envelope.Ok(new { resource = "migration", operation = "applied", applied, skipped });
    }
}