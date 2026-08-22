namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsLocalSettings
{
    public string? SignatureImagePath { get; set; }
}

public interface IExpirationsLocalSettingsService
{
    ExpirationsLocalSettings Load();
    void Save(ExpirationsLocalSettings settings);
}

public sealed class ExpirationsLocalSettingsService : IExpirationsLocalSettingsService
{
    private readonly AppDataPaths _paths;
    private readonly AtomicJsonFile _json;

    public ExpirationsLocalSettingsService(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _json = new AtomicJsonFile(logger ?? throw new ArgumentNullException(nameof(logger)));
    }

    public ExpirationsLocalSettings Load() =>
        _json.Load<ExpirationsLocalSettings>(_paths.ExpirationsLocalSettingsFile, out _)
        ?? new ExpirationsLocalSettings();

    public void Save(ExpirationsLocalSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _json.Save(_paths.ExpirationsLocalSettingsFile, settings);
    }
}
