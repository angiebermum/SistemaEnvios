using Google.Cloud.Firestore;

namespace ECSCommissionsMailer.FirestoreMigration;

public static class MigrationStateUpdates
{
    public const string DocumentPath = "system/migrationState";

    public static Dictionary<string, object> Completed(MigrationCounts counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = "completed",
            ["dataMigrated"] = true,
            ["completedAtUtc"] = FieldValue.ServerTimestamp,
            ["lastMigrationAtUtc"] = FieldValue.ServerTimestamp,
            ["errorSummary"] = null!,
            ["failedAtUtc"] = null!,
            ["settingsCount"] = (long)counts.Settings,
            ["brokersCount"] = (long)counts.Brokers,
            ["sessionBrokerItemsCount"] = (long)counts.SessionBrokerItems,
            ["recentSendsCount"] = (long)counts.RecentSends,
            ["paymentGenerationsCount"] = (long)counts.PaymentGenerations,
            ["paymentGenerationFilesCount"] = (long)counts.PaymentGenerationFiles
        };
    }

    public static Dictionary<string, object> Failed(string summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var safeSummary = summary.Length <= 2000 ? summary : summary[..2000];
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = "failed",
            ["dataMigrated"] = false,
            ["failedAtUtc"] = FieldValue.ServerTimestamp,
            ["errorSummary"] = safeSummary
        };
    }
}
