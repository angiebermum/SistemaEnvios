using System.Text.Json;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class InstallerInitializationTests
{
    [Fact]
    public void PackagedInitialMailResources_AreAvailableBesideApplication()
    {
        var seedPath = DirectoryMigrationService.GetSeedPath(AppContext.BaseDirectory);
        var signaturePath = Path.Combine(AppContext.BaseDirectory, "Data", "firma-inicial.png");

        Assert.True(File.Exists(seedPath), $"No se encontró la semilla publicada: {seedPath}");
        Assert.True(File.Exists(signaturePath), $"No se encontró la firma publicada: {signaturePath}");
    }

    [Fact]
    public void CleanInstallation_CreatesLocalDirectoriesAndInitialConfiguration()
    {
        using var scope = new TestDirectory();
        var paths = new AppDataPaths(scope.CreateDirectory("local-data"));
        var configuration = new ConfigurationService(paths, new FileLogger(paths), AppContext.BaseDirectory).Load();

        Assert.True(Directory.Exists(paths.RootDirectory));
        Assert.True(Directory.Exists(paths.LogsDirectory));
        Assert.True(Directory.Exists(paths.SignatureDirectory));
        Assert.True(File.Exists(paths.ConfigurationFile));
        Assert.Equal(AppConfiguration.CurrentEmailDirectorySeedVersion, configuration.EmailDirectorySeedVersion);
        Assert.NotEmpty(configuration.Brokers);
        Assert.NotNull(configuration.SignatureImagePath);
        Assert.True(File.Exists(configuration.SignatureImagePath));
        Assert.StartsWith(paths.SignatureDirectory, configuration.SignatureImagePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExistingLocalConfiguration_IsNotOverwrittenWhenSeedIsAlreadyApplied()
    {
        using var scope = new TestDirectory();
        var paths = new AppDataPaths(scope.CreateDirectory("local-data"));
        var service = new ConfigurationService(paths, new FileLogger(paths), AppContext.BaseDirectory);
        var configuration = service.Load();
        var localBrokerId = Guid.NewGuid();
        configuration.DefaultSubject = "Asunto local que debe conservarse";
        configuration.Brokers.Add(new Broker
        {
            Id = localBrokerId,
            Name = "Corredor local",
            PrimaryEmailAddresses = ["local@example.com"]
        });
        service.Save(configuration);
        var savedJson = File.ReadAllText(paths.ConfigurationFile);

        var reloaded = new ConfigurationService(paths, new FileLogger(paths), AppContext.BaseDirectory).Load();

        Assert.Equal(savedJson, File.ReadAllText(paths.ConfigurationFile));
        Assert.Equal("Asunto local que debe conservarse", reloaded.DefaultSubject);
        Assert.Contains(reloaded.Brokers, broker => broker.Id == localBrokerId);
    }

    [Fact]
    public void MissingInitialMailResource_ProducesClearControlledError()
    {
        using var scope = new TestDirectory();
        var emptyApplicationDirectory = scope.CreateDirectory("empty-application");
        var paths = new AppDataPaths(scope.CreateDirectory("local-data"));
        var service = new ConfigurationService(paths, new FileLogger(paths), emptyApplicationDirectory);

        var error = Assert.Throws<FileNotFoundException>(() => service.Load());

        Assert.Equal("No se encontró el directorio inicial de correos.", error.Message);
        Assert.Equal(DirectoryMigrationService.GetSeedPath(emptyApplicationDirectory), error.FileName);
    }

    [Fact]
    public void InitialMailResource_UsesOnlyRelativePackagedPaths()
    {
        var seedPath = DirectoryMigrationService.GetSeedPath(AppContext.BaseDirectory);
        using var seed = JsonDocument.Parse(File.ReadAllText(seedPath));
        var signaturePath = seed.RootElement.GetProperty("signatureFile").GetString();

        Assert.Equal("Data/firma-inicial.png", signaturePath);
        Assert.NotNull(signaturePath);
        Assert.False(Path.IsPathFullyQualified(DirectoryMigrationService.SeedRelativePath));
        Assert.False(Path.IsPathFullyQualified(signaturePath));
    }

    [Fact]
    public void PackagedApplication_DoesNotContainLocalUserState()
    {
        var forbiddenFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "configuracion.json",
            "sesion-actual.json",
            "envios-recientes.json",
            "generaciones-detalles-pago.json",
            "firebase-runtime.json",
            "firebase-refresh-token.dat",
            "firebase-session.json",
            "firebase-auth.json",
            "user-session.json",
            "refresh-token.json",
            "application_default_credentials.json",
            "credentials.json",
            "adc.json",
            "service-account.json",
            "service_account.json",
            "firebase-adminsdk.json"
        };
        var forbiddenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Logs",
            "Backups",
            "ArchivosEnviados",
            "TempEdits",
            "TempView"
        };

        var packagedFiles = Directory.EnumerateFiles(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                var fileName = System.IO.Path.GetFileName(path);
                return forbiddenFileNames.Contains(fileName) ||
                       fileName.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase) ||
                       (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                        (fileName.Contains("service-account", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Contains("service_account", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Contains("refresh-token", StringComparison.OrdinalIgnoreCase) ||
                         fileName.Contains("refresh_token", StringComparison.OrdinalIgnoreCase)));
            })
            .ToList();
        var packagedDirectories = Directory.EnumerateDirectories(AppContext.BaseDirectory, "*", SearchOption.AllDirectories)
            .Where(path => forbiddenDirectories.Contains(System.IO.Path.GetFileName(path)))
            .ToList();

        Assert.Empty(packagedFiles);
        Assert.Empty(packagedDirectories);
    }

    private sealed class TestDirectory : IDisposable
    {
        public TestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"ECSCommissionsMailer-installer-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string CreateDirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
