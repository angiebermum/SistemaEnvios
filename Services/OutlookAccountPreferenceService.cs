namespace ECS.CommissionsMailer.Services;

internal interface IOutlookAccountPreferenceStore
{
    string? LoadSelectedAccountEmail();
    void SaveSelectedAccountEmail(string emailAddress);
}

internal sealed class OutlookAccountPreferenceService : IOutlookAccountPreferenceStore
{
    private readonly AppDataPaths _paths;
    private readonly AtomicJsonFile _json;
    private readonly FileLogger _logger;

    public OutlookAccountPreferenceService(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths;
        _json = new AtomicJsonFile(logger);
        _logger = logger;
    }

    public string? LoadSelectedAccountEmail()
    {
        var preferences = _json.Load<OutlookAccountPreferences>(_paths.OutlookPreferencesFile, out var warning);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            _logger.Info(warning);
        }

        return Normalize(preferences?.SelectedAccountEmail);
    }

    public void SaveSelectedAccountEmail(string emailAddress)
    {
        var normalized = Normalize(emailAddress) ??
            throw new ArgumentException("La cuenta de Outlook seleccionada no contiene una dirección válida.", nameof(emailAddress));
        _json.Save(_paths.OutlookPreferencesFile, new OutlookAccountPreferences
        {
            SelectedAccountEmail = normalized
        });
    }

    private static string? Normalize(string? emailAddress) =>
        string.IsNullOrWhiteSpace(emailAddress) ? null : emailAddress.Trim();

    private sealed class OutlookAccountPreferences
    {
        public string? SelectedAccountEmail { get; set; }
    }
}
