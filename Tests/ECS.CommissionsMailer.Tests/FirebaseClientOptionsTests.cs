using System.Text.Json;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;

namespace ECS.CommissionsMailer.Tests;

[CollectionDefinition("Firebase client options environment", DisableParallelization = true)]
public sealed class FirebaseClientOptionsEnvironmentCollection
{
    public const string Name = "Firebase client options environment";
}

[Collection(FirebaseClientOptionsEnvironmentCollection.Name)]
public sealed class FirebaseClientOptionsTests
{
    private static readonly string[] EnvironmentVariableNames =
    [
        FirebaseClientOptions.ProjectIdEnvironmentVariable,
        FirebaseClientOptions.DatabaseIdEnvironmentVariable,
        FirebaseClientOptions.ApiKeyEnvironmentVariable,
        FirebaseClientOptions.RuntimeModeEnvironmentVariable
    ];

    [Fact]
    public void CleanPcWithoutRuntimeFileUsesProductionDefaults()
    {
        using var environment = EnvironmentScope.Clear(EnvironmentVariableNames);
        var missingConfiguration = Path.Combine(
            Path.GetTempPath(),
            $"ecs-missing-firebase-runtime-{Guid.NewGuid():N}.json");

        var options = FirebaseClientOptions.Load(missingConfiguration);

        Assert.False(File.Exists(missingConfiguration));
        Assert.Equal("essential-4eccd", options.ProjectId);
        Assert.Equal("(default)", options.DatabaseId);
        Assert.Equal(RuntimeDataMode.FirestorePrimary, options.RuntimeDataMode);
        Assert.False(string.IsNullOrWhiteSpace(options.FirebaseApiKey));
        Assert.False(options.RequireLegacyCutoverPreflight);
        options.ValidateForAuthenticatedMode();
    }

    [Fact]
    public void ExistingRuntimeFileOverridesProductionDefaults()
    {
        using var environment = EnvironmentScope.Clear(EnvironmentVariableNames);
        using var configuration = RuntimeConfigurationFile.Create(new
        {
            projectId = "file-project",
            databaseId = "file-database",
            firebaseApiKey = "file-api-key",
            runtimeDataMode = "FirestoreShadowRead",
            rememberSession = false,
            requireLegacyCutoverPreflight = true
        });

        var options = FirebaseClientOptions.Load(configuration.Path);

        Assert.Equal("file-project", options.ProjectId);
        Assert.Equal("file-database", options.DatabaseId);
        Assert.Equal("file-api-key", options.FirebaseApiKey);
        Assert.Equal(RuntimeDataMode.FirestoreShadowRead, options.RuntimeDataMode);
        Assert.False(options.RememberSession);
        Assert.True(options.RequireLegacyCutoverPreflight);
    }

    [Fact]
    public void EnvironmentVariablesContinueToOverrideRuntimeFile()
    {
        using var configuration = RuntimeConfigurationFile.Create(new
        {
            projectId = "file-project",
            databaseId = "file-database",
            firebaseApiKey = "file-api-key",
            runtimeDataMode = "JsonOnly"
        });
        using var environment = new EnvironmentScope(new Dictionary<string, string?>
        {
            [FirebaseClientOptions.ProjectIdEnvironmentVariable] = "environment-project",
            [FirebaseClientOptions.DatabaseIdEnvironmentVariable] = "environment-database",
            [FirebaseClientOptions.ApiKeyEnvironmentVariable] = "environment-api-key",
            [FirebaseClientOptions.RuntimeModeEnvironmentVariable] = "FirestorePrimary"
        });

        var options = FirebaseClientOptions.Load(configuration.Path);

        Assert.Equal("environment-project", options.ProjectId);
        Assert.Equal("environment-database", options.DatabaseId);
        Assert.Equal("environment-api-key", options.FirebaseApiKey);
        Assert.Equal(RuntimeDataMode.FirestorePrimary, options.RuntimeDataMode);
    }

    private sealed class RuntimeConfigurationFile : IDisposable
    {
        private RuntimeConfigurationFile(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static RuntimeConfigurationFile Create(object value)
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ecs-firebase-runtime-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(value));
            return new RuntimeConfigurationFile(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string?> _originalValues;

        public EnvironmentScope(IReadOnlyDictionary<string, string?> values)
        {
            _originalValues = values.Keys.ToDictionary(
                name => name,
                Environment.GetEnvironmentVariable,
                StringComparer.Ordinal);

            foreach (var pair in values)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }

        public static EnvironmentScope Clear(IEnumerable<string> names) =>
            new(names.ToDictionary(name => name, _ => (string?)null, StringComparer.Ordinal));

        public void Dispose()
        {
            foreach (var pair in _originalValues)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }
        }
    }
}
