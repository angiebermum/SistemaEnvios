namespace ECSCommissionsMailer.FirestoreMigration;

public static class MigrationConstants
{
    public const int FirestoreSchemaVersion = 1;
    public const string ToolVersion = "2.0.0";

    public static readonly IReadOnlyList<string> RequiredSourceFiles =
    [
        "configuracion.json",
        "sesion-actual.json",
        "envios-recientes.json",
        "generaciones-detalles-pago.json"
    ];

    public static readonly IReadOnlySet<string> ForbiddenFirestoreFields =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SignatureImagePath",
            "GeneralWorkbookPath",
            "GeneratedOutputDirectory",
            "AttachmentPaths",
            "GeneratedAttachmentPaths",
            "ArchivedAttachmentPaths",
            "SourceWorkbookPath",
            "OutputDirectory",
            "OutputPath"
        };
}
