using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed class ConfigurationService
{
    private readonly AppDataPaths _paths;
    private readonly AtomicJsonFile _json;
    private readonly DirectoryMigrationService _migration;

    public ConfigurationService(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths;
        _json = new AtomicJsonFile(logger);
        _migration = new DirectoryMigrationService(paths, logger);
    }

    public string? LastWarning { get; private set; }

    public AppConfiguration Load()
    {
        var configuration = _json.Load<AppConfiguration>(_paths.ConfigurationFile, out var warning);
        LastWarning = warning;
        if (configuration is null)
        {
            configuration = AppConfiguration.CreateDefault();
        }

        DirectoryMigrationService.Normalize(configuration);
        var result = _migration.ApplyIfNeeded(configuration);
        LastWarning = string.Join(Environment.NewLine + Environment.NewLine,
            new[] { LastWarning, result.Warning }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return configuration;
    }

    public void Save(AppConfiguration configuration) => _json.Save(_paths.ConfigurationFile, configuration);
}
