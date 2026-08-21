namespace ECS.CommissionsMailer.Services;

internal sealed record OutlookAccountSelectionState(
    IReadOnlyList<string> AvailableAccounts,
    string? SelectedAccountEmail)
{
    public bool RequiresExplicitSelection => AvailableAccounts.Count > 1 &&
        string.IsNullOrWhiteSpace(SelectedAccountEmail);
}

internal sealed class OutlookAccountSelectionManager
{
    private readonly IOutlookAccountPreferenceStore _preferences;
    private readonly object _sync = new();
    private IReadOnlyList<string> _availableAccounts = [];
    private string? _selectedAccountEmail;
    private bool _initialized;

    public OutlookAccountSelectionManager(IOutlookAccountPreferenceStore preferences) =>
        _preferences = preferences;

    public OutlookAccountSelectionState Refresh(IEnumerable<string> accountEmails)
    {
        var availableAccounts = NormalizeAccounts(accountEmails);
        var preferredAccount = _preferences.LoadSelectedAccountEmail();
        var selectedAccount = Find(availableAccounts, preferredAccount);
        if (selectedAccount is null && availableAccounts.Count == 1)
        {
            selectedAccount = availableAccounts[0];
        }

        lock (_sync)
        {
            _availableAccounts = availableAccounts;
            _selectedAccountEmail = selectedAccount;
            _initialized = true;
        }

        if (selectedAccount is not null &&
            !string.Equals(selectedAccount, preferredAccount, StringComparison.OrdinalIgnoreCase))
        {
            _preferences.SaveSelectedAccountEmail(selectedAccount);
        }

        return new OutlookAccountSelectionState(availableAccounts, selectedAccount);
    }

    public void ClearAvailableAccounts()
    {
        lock (_sync)
        {
            _availableAccounts = [];
            _selectedAccountEmail = null;
            _initialized = true;
        }
    }

    public string Select(string emailAddress)
    {
        string? selectedAccount;
        lock (_sync)
        {
            selectedAccount = Find(_availableAccounts, emailAddress);
            if (selectedAccount is null)
            {
                throw CreateUnavailableException(
                    "La cuenta seleccionada no está disponible en la lista actual de Outlook.");
            }

        }

        _preferences.SaveSelectedAccountEmail(selectedAccount);
        lock (_sync)
        {
            _selectedAccountEmail = selectedAccount;
        }
        return selectedAccount;
    }

    public string RequireAvailableSelection(IEnumerable<string> currentAccountEmails)
    {
        var currentAccounts = NormalizeAccounts(currentAccountEmails);
        string? selectedAccount;
        bool initialized;
        lock (_sync)
        {
            initialized = _initialized;
            selectedAccount = _selectedAccountEmail;
        }

        if (!initialized)
        {
            selectedAccount = Refresh(currentAccounts).SelectedAccountEmail;
        }

        if (string.IsNullOrWhiteSpace(selectedAccount))
        {
            throw CreateUnavailableException(
                "Seleccione explícitamente una cuenta de Outlook antes de enviar.");
        }

        var availableSelection = Find(currentAccounts, selectedAccount);
        if (availableSelection is null)
        {
            throw CreateUnavailableException(
                $"La cuenta seleccionada {selectedAccount} ya no está disponible en Outlook.");
        }

        return availableSelection;
    }

    internal OutlookAccountSelectionState Snapshot()
    {
        lock (_sync)
        {
            return new OutlookAccountSelectionState(_availableAccounts.ToArray(), _selectedAccountEmail);
        }
    }

    private static IReadOnlyList<string> NormalizeAccounts(IEnumerable<string> accountEmails)
    {
        ArgumentNullException.ThrowIfNull(accountEmails);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in accountEmails)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (normalized is not null && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private static string? Find(IReadOnlyList<string> accounts, string? emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            return null;
        }

        return accounts.FirstOrDefault(value =>
            value.Equals(emailAddress.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static OutlookIntegrationException CreateUnavailableException(string detail) =>
        new(
            Models.OutlookFailureReason.SendingAccountUnavailable,
            $"{detail} No se envió ningún correo para evitar usar otra cuenta.");
}
