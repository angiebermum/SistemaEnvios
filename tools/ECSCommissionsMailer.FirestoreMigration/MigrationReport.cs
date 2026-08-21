using System.Text;
using System.Text.Json;

namespace ECSCommissionsMailer.FirestoreMigration;

public sealed class MigrationReport
{
    public string MigrationId { get; set; } = string.Empty;
    public DateTimeOffset StartUtc { get; set; }
    public DateTimeOffset? EndUtc { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string SourceDirectory { get; set; } = string.Empty;
    public string ProjectId { get; set; } = string.Empty;
    public string DatabaseId { get; set; } = string.Empty;
    public string? BackupDirectory { get; set; }
    public Dictionary<string, string> SourceFileHashes { get; set; } = new(StringComparer.Ordinal);
    public MigrationCounts? LocalCounts { get; set; }
    public MigrationCounts? FirestoreCounts { get; set; }
    public bool? BootstrapSchemaExists { get; set; }
    public bool? BootstrapMigrationStateExists { get; set; }
    public int PreflightToCreate { get; set; }
    public int PreflightIdentical { get; set; }
    public int PreflightConflicts { get; set; }
    public List<string> Created { get; set; } = [];
    public List<string> SkippedIdentical { get; set; } = [];
    public List<DocumentMismatch> Conflicts { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    public Dictionary<string, int> LocalOnlyFieldsExcluded { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, bool> Verification { get; set; } = new(StringComparer.Ordinal);
    public List<DocumentMismatch> MismatchDetails { get; set; } = [];
    public bool? DecimalPrecisionValid { get; set; }
    public bool? IdsValid { get; set; }
    public bool? ReferencesValid { get; set; }
    public bool? IdempotentRerun { get; set; }
    public string Status { get; set; } = "started";
}

public static class MigrationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static (string JsonPath, string TextPath) Write(MigrationReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        report.EndUtc ??= DateTimeOffset.UtcNow;
        var jsonPath = Path.Combine(directory, "migration-report.json");
        var textPath = Path.Combine(directory, "migration-report.txt");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions), Encoding.UTF8);
        File.WriteAllText(textPath, ToText(report), Encoding.UTF8);
        return (jsonPath, textPath);
    }

    private static string ToText(MigrationReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"MigrationId: {report.MigrationId}");
        text.AppendLine($"Status: {report.Status}");
        text.AppendLine($"Mode: {report.Mode}");
        text.AppendLine($"Start UTC: {report.StartUtc:O}");
        text.AppendLine($"End UTC: {report.EndUtc:O}");
        text.AppendLine($"Source directory: {report.SourceDirectory}");
        text.AppendLine($"ProjectId: {report.ProjectId}");
        text.AppendLine($"DatabaseId: {report.DatabaseId}");
        text.AppendLine($"Backup directory: {report.BackupDirectory ?? "(none)"}");
        text.AppendLine();
        text.AppendLine("Source SHA256:");
        foreach (var pair in report.SourceFileHashes)
        {
            text.AppendLine($"  {pair.Key}: {pair.Value}");
        }

        AppendCounts(text, "Local counts", report.LocalCounts);
        AppendCounts(text, "Firestore counts", report.FirestoreCounts);
        text.AppendLine();
        text.AppendLine($"Bootstrap system/schema: {report.BootstrapSchemaExists}");
        text.AppendLine($"Bootstrap system/migrationState: {report.BootstrapMigrationStateExists}");
        text.AppendLine($"Preflight create: {report.PreflightToCreate}");
        text.AppendLine($"Preflight identical: {report.PreflightIdentical}");
        text.AppendLine($"Preflight conflicts: {report.PreflightConflicts}");
        text.AppendLine($"Created: {report.Created.Count}");
        text.AppendLine($"SkippedIdentical: {report.SkippedIdentical.Count}");
        text.AppendLine($"Conflicts: {report.Conflicts.Count}");
        text.AppendLine($"Errors: {report.Errors.Count}");
        text.AppendLine($"Mismatches: {report.MismatchDetails.Count}");
        text.AppendLine($"Decimal precision valid: {report.DecimalPrecisionValid}");
        text.AppendLine($"IDs valid: {report.IdsValid}");
        text.AppendLine($"References valid: {report.ReferencesValid}");
        text.AppendLine($"Idempotent rerun: {report.IdempotentRerun}");
        text.AppendLine();
        text.AppendLine("LOCAL_ONLY excluded:");
        foreach (var pair in report.LocalOnlyFieldsExcluded)
        {
            text.AppendLine($"  {pair.Key}: {pair.Value}");
        }

        text.AppendLine("Verification:");
        foreach (var pair in report.Verification)
        {
            text.AppendLine($"  {pair.Key}: {(pair.Value ? "MATCH" : "MISMATCH")}");
        }

        foreach (var mismatch in report.MismatchDetails.Concat(report.Conflicts))
        {
            text.AppendLine($"{mismatch.State}: {mismatch.Path}");
            foreach (var detail in mismatch.Differences)
            {
                text.AppendLine($"  - {detail}");
            }
        }

        foreach (var error in report.Errors)
        {
            text.AppendLine($"ERROR: {error}");
        }

        return text.ToString();
    }

    private static void AppendCounts(StringBuilder text, string title, MigrationCounts? counts)
    {
        text.AppendLine();
        text.AppendLine($"{title}:");
        if (counts is null)
        {
            text.AppendLine("  (unavailable)");
            return;
        }

        text.AppendLine($"  settings: {counts.Settings}");
        text.AppendLine($"  brokers: {counts.Brokers}");
        text.AppendLine($"  session: {counts.Session}");
        text.AppendLine($"  sessionBrokerItems: {counts.SessionBrokerItems}");
        text.AppendLine($"  recentSends: {counts.RecentSends}");
        text.AppendLine($"  paymentGenerations: {counts.PaymentGenerations}");
        text.AppendLine($"  paymentGenerationFiles: {counts.PaymentGenerationFiles}");
    }
}
