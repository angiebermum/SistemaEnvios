using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsBrokerProfileWindow : Window
{
    private readonly IExpirationsBrokerConfigurationService _service;
    private readonly ExpirationsAssistantValidationService _validation = new();
    private bool _allowClose;
    internal readonly ExpirationsBrokerProfileState State;

    public ExpirationsBrokerProfileWindow(
        IExpirationsBrokerConfigurationService service,
        ExpirationsBrokerConfigurationItem configuration)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        State = new ExpirationsBrokerProfileState(configuration, _validation);
        InitializeComponent();
        DataContext = State;
    }

    public ExpirationsBrokerConfigurationItem? SavedConfiguration { get; private set; }
    public bool WasPersisted { get; private set; }

    private void AddAssistant_Click(object sender, RoutedEventArgs e)
    {
        var editor = new ExpirationsAssistantEditorWindow(_validation) { Owner = this };
        if (editor.ShowDialog() == true && editor.Assistant is { } assistant)
            ApplyAssistant(assistant);
    }

    private void EditAssistant_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedAssistant is not { } selected)
            return;
        var editor = new ExpirationsAssistantEditorWindow(_validation, selected) { Owner = this };
        if (editor.ShowDialog() == true && editor.Assistant is { } assistant)
            ApplyAssistant(assistant);
    }

    private void ApplyAssistant(ExpirationsAssistant assistant)
    {
        var errors = State.AddOrReplaceAssistant(assistant);
        if (errors.Count > 0)
        {
            MessageBox.Show(
                string.Join(Environment.NewLine, errors),
                "Asistente de Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ToggleAssistant_Click(object sender, RoutedEventArgs e) => State.ToggleSelectedAssistant();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        State.SetBusy(true);
        try
        {
            var result = await _service.SaveAsync(State.BuildConfiguration());
            if (result.Outcome == ExpirationsBrokerConfigurationSaveOutcome.ValidationFailed)
            {
                MessageBox.Show(result.Message, "Revise los asistentes", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (result.Outcome == ExpirationsBrokerConfigurationSaveOutcome.ConcurrencyConflict)
            {
                State.Load(result.Configuration);
                MessageBox.Show(result.Message, "Configuración actualizada", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            State.MarkPersisted(result.Configuration);
            SavedConfiguration = result.Configuration;
            WasPersisted = result.WasPersisted;
            MessageBox.Show(result.Message, "Vencimientos", MessageBoxButton.OK, MessageBoxImage.Information);
            _allowClose = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible guardar la configuración.\n\n{ex.Message}",
                "Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !State.HasUnsavedChanges)
            return;
        var answer = MessageBox.Show(
            "Hay cambios sin guardar. ¿Desea descartarlos?",
            "Cambios sin guardar",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        State.DiscardChanges();
    }
}

internal sealed class ExpirationsBrokerProfileState : INotifyPropertyChanged
{
    private readonly ExpirationsAssistantValidationService _validation;
    private ExpirationsBrokerConfigurationItem _persisted;
    private bool _isActive;
    private ExpirationsAssistant? _selectedAssistant;
    private bool _isBusy;

    public ExpirationsBrokerProfileState(
        ExpirationsBrokerConfigurationItem configuration,
        ExpirationsAssistantValidationService validation)
    {
        _validation = validation ?? throw new ArgumentNullException(nameof(validation));
        _persisted = CloneConfiguration(configuration);
        Assistants = [];
        Load(configuration);
    }

    public ObservableCollection<ExpirationsAssistant> Assistants { get; }
    public Guid BrokerId => _persisted.BrokerId;
    public string Name => _persisted.Name;
    public string PrimaryEmailsText => _persisted.PrimaryEmailsText;
    public Visibility MissingPrimaryEmailVisibility => _persisted.HasPrimaryEmail
        ? Visibility.Collapsed
        : Visibility.Visible;
    public bool IsBusy => _isBusy;
    public bool HasUnsavedChanges => _isActive != _persisted.IsActive ||
        !AssistantListsEqual(Assistants, _persisted.Assistants);
    public bool CanEditConfiguration => !IsBusy;
    public bool CanSave => !IsBusy;
    public bool CanEditAssistant => !IsBusy && SelectedAssistant is not null;
    public bool CanToggleAssistant => !IsBusy && SelectedAssistant is not null;
    public string ChangesText => HasUnsavedChanges ? "Hay cambios sin guardar." : "Sin cambios pendientes.";
    public int ActiveAssistantCount => Assistants.Count(value => value.IsActive);
    public int TotalAssistantCount => Assistants.Count;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            Notify();
            NotifyChanges();
        }
    }

    public ExpirationsAssistant? SelectedAssistant
    {
        get => _selectedAssistant;
        set
        {
            if (ReferenceEquals(_selectedAssistant, value)) return;
            _selectedAssistant = value;
            Notify();
            Notify(nameof(CanEditAssistant));
            Notify(nameof(CanToggleAssistant));
        }
    }

    public IReadOnlyList<string> AddOrReplaceAssistant(ExpirationsAssistant assistant)
    {
        ArgumentNullException.ThrowIfNull(assistant);
        var candidate = Assistants
            .Where(value => value.Id != assistant.Id)
            .Select(CopyAssistant)
            .Append(CopyAssistant(assistant))
            .ToList();
        var validation = _validation.ValidateAndNormalize(_persisted.PrimaryEmailAddresses, candidate);
        if (!validation.IsValid)
            return validation.Errors;
        ReplaceAssistants(validation.Assistants);
        SelectedAssistant = Assistants.First(value => value.Id == assistant.Id);
        NotifyChanges();
        return [];
    }

    public void ToggleSelectedAssistant()
    {
        if (SelectedAssistant is not { } selected)
            return;
        _ = AddOrReplaceAssistant(new ExpirationsAssistant
        {
            Id = selected.Id,
            Name = selected.Name,
            Email = selected.Email,
            IsActive = !selected.IsActive
        });
    }

    public ExpirationsBrokerConfigurationItem BuildConfiguration() => new()
    {
        BrokerId = _persisted.BrokerId,
        Name = _persisted.Name,
        PrimaryEmailAddresses = _persisted.PrimaryEmailAddresses.ToList(),
        IsActive = IsActive,
        Assistants = Assistants.Select(CopyAssistant).ToList(),
        HasExplicitProfile = _persisted.HasExplicitProfile,
        ProfileUpdateTime = _persisted.ProfileUpdateTime,
        ProfileCreatedAtUtc = _persisted.ProfileCreatedAtUtc,
        ProfileUpdatedAtUtc = _persisted.ProfileUpdatedAtUtc
    };

    public void MarkPersisted(ExpirationsBrokerConfigurationItem configuration) => Load(configuration);
    public void DiscardChanges() => Load(_persisted);

    public void Load(ExpirationsBrokerConfigurationItem configuration)
    {
        _persisted = CloneConfiguration(configuration);
        _isActive = configuration.IsActive;
        ReplaceAssistants(configuration.Assistants);
        SelectedAssistant = null;
        Notify(string.Empty);
    }

    public void SetBusy(bool value)
    {
        if (_isBusy == value) return;
        _isBusy = value;
        Notify(nameof(IsBusy));
        Notify(nameof(CanEditConfiguration));
        Notify(nameof(CanSave));
        Notify(nameof(CanEditAssistant));
        Notify(nameof(CanToggleAssistant));
    }

    private void ReplaceAssistants(IEnumerable<ExpirationsAssistant> assistants)
    {
        Assistants.Clear();
        foreach (var assistant in assistants
                     .Select(CopyAssistant)
                     .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(value => value.Email, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(value => value.Id))
        {
            Assistants.Add(assistant);
        }
        Notify(nameof(Assistants));
        Notify(nameof(ActiveAssistantCount));
        Notify(nameof(TotalAssistantCount));
    }

    private void NotifyChanges()
    {
        Notify(nameof(HasUnsavedChanges));
        Notify(nameof(ChangesText));
        Notify(nameof(ActiveAssistantCount));
        Notify(nameof(TotalAssistantCount));
    }

    private static bool AssistantListsEqual(
        IEnumerable<ExpirationsAssistant> left,
        IEnumerable<ExpirationsAssistant> right) => left
        .OrderBy(value => value.Id)
        .Select(value => (value.Id, value.Name, value.Email, value.IsActive))
        .SequenceEqual(right.OrderBy(value => value.Id)
            .Select(value => (value.Id, value.Name, value.Email, value.IsActive)));

    private static ExpirationsBrokerConfigurationItem CloneConfiguration(
        ExpirationsBrokerConfigurationItem value) => new()
    {
        BrokerId = value.BrokerId,
        Name = value.Name,
        PrimaryEmailAddresses = value.PrimaryEmailAddresses.ToList(),
        IsActive = value.IsActive,
        Assistants = value.Assistants.Select(CopyAssistant).ToList(),
        HasExplicitProfile = value.HasExplicitProfile,
        ProfileUpdateTime = value.ProfileUpdateTime,
        ProfileCreatedAtUtc = value.ProfileCreatedAtUtc,
        ProfileUpdatedAtUtc = value.ProfileUpdatedAtUtc
    };

    private static ExpirationsAssistant CopyAssistant(ExpirationsAssistant value) => new()
    {
        Id = value.Id,
        Name = value.Name,
        Email = value.Email,
        IsActive = value.IsActive
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
