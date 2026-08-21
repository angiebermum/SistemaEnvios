namespace ECS.CommissionsMailer.Infrastructure.Firestore;

public static class FirestorePaths
{
    public const string SystemCollection = "system";
    public const string SchemaDocument = "schema";
    public const string MigrationStateDocument = "migrationState";
    public const string SettingsCollection = "settings";
    public const string CommissionsSettingsDocument = "commissions";
    public const string BrokersCollection = "brokers";
    public const string SessionsCollection = "sessions";
    public const string CurrentSessionDocument = "current";
    public const string BrokerItemsSubcollection = "brokerItems";
    public const string RecentSendsCollection = "recentSends";
    public const string PaymentGenerationsCollection = "paymentGenerations";
    public const string GenerationFilesSubcollection = "files";

    public static string GuidDocumentId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("El identificador del documento no puede estar vacío.", nameof(id));
        }

        return id.ToString("D");
    }
}
