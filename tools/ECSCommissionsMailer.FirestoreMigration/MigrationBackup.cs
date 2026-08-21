using System.Security.Cryptography;
using System.Text.Json;

namespace ECSCommissionsMailer.FirestoreMigration;

public sealed record BackupManifestFile(
    string Name,
    string OriginalPath,
    string Sha256,
    long Size);

public sealed record MigrationManifest(
    string MigrationId,
    DateTimeOffset TimestampUtc,
    string ProjectId,
    string DatabaseId,
    int SchemaVersion,
    string ToolVersion,
    IReadOnlyList<BackupManifestFile> Files);

public sealed record MigrationBackup(
    string MigrationId,
    string Directory,
    string ManifestPath,
    string ManifestSha256,
    MigrationManifest Manifest);

public static class MigrationBackupService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static MigrationBackup Create(MigrationSource source, string projectId, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(source);
        var now = DateTimeOffset.UtcNow;
        var migrationId = $"{now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}";
        var backupDirectory = Path.Combine(source.Directory, "migration-backups", migrationId);
        Directory.CreateDirectory(backupDirectory);

        foreach (var file in source.Files)
        {
            var currentHash = HashFile(file.OriginalPath);
            if (!string.Equals(file.Sha256, currentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"'{file.Name}' cambió después del preflight local; se canceló el backup.");
            }

            var destination = Path.Combine(backupDirectory, file.Name);
            File.Copy(file.OriginalPath, destination, overwrite: false);
            var copiedHash = HashFile(destination);
            if (!string.Equals(file.Sha256, copiedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"El SHA256 de la copia de '{file.Name}' no coincide con el original.");
            }
        }

        var manifest = new MigrationManifest(
            migrationId,
            now,
            projectId,
            databaseId,
            MigrationConstants.FirestoreSchemaVersion,
            MigrationConstants.ToolVersion,
            source.Files.Select(file => new BackupManifestFile(
                file.Name,
                file.OriginalPath,
                file.Sha256,
                file.Size)).ToList());
        var manifestPath = Path.Combine(backupDirectory, "manifest.json");
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        var manifestHash = HashFile(manifestPath);
        return new MigrationBackup(migrationId, backupDirectory, manifestPath, manifestHash, manifest);
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
