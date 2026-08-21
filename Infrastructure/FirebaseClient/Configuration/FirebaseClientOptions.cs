using System.Text.Json;
using System.Text.Json.Serialization;

namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeDataMode>))]
public enum RuntimeDataMode
{
    JsonOnly,
    FirestoreShadowRead,
    FirestorePrimary
}

public sealed class FirebaseClientOptions
{
    public const string ProjectIdEnvironmentVariable = "ECS_FIREBASE_PROJECT_ID";
    public const string DatabaseIdEnvironmentVariable = "ECS_FIRESTORE_DATABASE_ID";
    public const string ApiKeyEnvironmentVariable = "ECS_FIREBASE_API_KEY";
    public const string RuntimeModeEnvironmentVariable = "ECS_RUNTIME_DATA_MODE";
    public const string ProductionProjectId = "essential-4eccd";
    public const string DefaultDatabaseId = "(default)";

    // Firebase client API keys identify the public client. Data authorization
    // continues to depend on Firebase Authentication and Firestore Security Rules.
    private const string ProductionFirebaseApiKey = "AIzaSyCL-a-2q1JqJJ81yO9Y9Q41ZGHSz3rK5yE";

    public string? ProjectId { get; set; } = ProductionProjectId;
    public string DatabaseId { get; set; } = DefaultDatabaseId;
    public string? FirebaseApiKey { get; set; } = ProductionFirebaseApiKey;
    public RuntimeDataMode RuntimeDataMode { get; set; } = RuntimeDataMode.FirestorePrimary;
    public bool RememberSession { get; set; } = true;
    public bool RequireLegacyCutoverPreflight { get; set; }

    public static FirebaseClientOptions Load(string configurationPath)
    {
        FirebaseClientOptions options;
        if (File.Exists(configurationPath))
        {
            var json = File.ReadAllText(configurationPath);
            options = JsonSerializer.Deserialize<FirebaseClientOptions>(json, JsonOptions())
                ?? new FirebaseClientOptions();
        }
        else
        {
            options = new FirebaseClientOptions();
        }

        options.ProjectId = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ProjectIdEnvironmentVariable),
            options.ProjectId);
        options.DatabaseId = FirstNonEmpty(
            Environment.GetEnvironmentVariable(DatabaseIdEnvironmentVariable),
            options.DatabaseId,
            DefaultDatabaseId)!;
        options.FirebaseApiKey = FirstNonEmpty(
            Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable),
            options.FirebaseApiKey);
        var modeText = Environment.GetEnvironmentVariable(RuntimeModeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(modeText))
        {
            if (!Enum.TryParse<RuntimeDataMode>(modeText, true, out var mode))
            {
                throw new InvalidDataException(
                    $"{RuntimeModeEnvironmentVariable} no contiene un modo válido.");
            }

            options.RuntimeDataMode = mode;
        }

        return options;
    }

    public void ValidateForAuthenticatedMode()
    {
        if (RuntimeDataMode == RuntimeDataMode.JsonOnly)
        {
            return;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(ProjectId)) missing.Add(nameof(ProjectId));
        if (string.IsNullOrWhiteSpace(DatabaseId)) missing.Add(nameof(DatabaseId));
        if (string.IsNullOrWhiteSpace(FirebaseApiKey)) missing.Add(nameof(FirebaseApiKey));
        if (missing.Count > 0)
        {
            throw new FirebaseConfigurationException(
                $"La configuración de Firebase está incompleta: {string.Join(", ", missing)}.");
        }
    }

    internal static JsonSerializerOptions JsonOptions() => new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed class FirebaseConfigurationException(string message) : InvalidOperationException(message);
