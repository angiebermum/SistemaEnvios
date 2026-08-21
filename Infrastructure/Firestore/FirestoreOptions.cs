namespace ECS.CommissionsMailer.Infrastructure.Firestore;

/// <summary>
/// Configuración externa para la infraestructura Firestore de comisiones.
/// El runtime WPF no crea ni consume esta configuración durante la fase 1.
/// </summary>
public sealed class FirestoreOptions
{
    public const string DefaultDatabaseId = "(default)";
    public const string ProjectIdEnvironmentVariable = "ECS_FIRESTORE_PROJECT_ID";
    public const string DatabaseIdEnvironmentVariable = "ECS_FIRESTORE_DATABASE_ID";
    public const string EnabledEnvironmentVariable = "ECS_FIRESTORE_ENABLED";

    public string? ProjectId { get; init; }
    public string DatabaseId { get; init; } = DefaultDatabaseId;
    public bool Enabled { get; init; } = false;

    public static FirestoreOptions FromEnvironment() => new()
    {
        ProjectId = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ProjectIdEnvironmentVariable),
            Environment.GetEnvironmentVariable("GOOGLE_CLOUD_PROJECT")),
        DatabaseId = FirstNonEmpty(
            Environment.GetEnvironmentVariable(DatabaseIdEnvironmentVariable),
            DefaultDatabaseId)!,
        Enabled = bool.TryParse(
            Environment.GetEnvironmentVariable(EnabledEnvironmentVariable),
            out var enabled) && enabled
    };

    public void ValidateForConnection()
    {
        if (!Enabled)
        {
            throw new InvalidOperationException(
                "Firestore está deshabilitado. En la fase 1 debe habilitarse explícitamente solo para herramientas separadas.");
        }

        if (string.IsNullOrWhiteSpace(ProjectId))
        {
            throw new InvalidOperationException(
                $"ProjectId es obligatorio y debe configurarse externamente con {ProjectIdEnvironmentVariable}, GOOGLE_CLOUD_PROJECT o un argumento de herramienta.");
        }

        if (string.IsNullOrWhiteSpace(DatabaseId))
        {
            throw new InvalidOperationException("DatabaseId no puede estar vacío.");
        }
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
