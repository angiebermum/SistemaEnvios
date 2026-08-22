using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsRoutingAdministrationWindow : Window
{
    private readonly IExpirationsRoutingAdministrationService _associations;
    private readonly IExpirationsBrokerConfigurationService _brokers;
    private readonly Guid? _initialBrokerId;
    internal readonly ExpirationsRoutingAdministrationState State = new();

    public ExpirationsRoutingAdministrationWindow(
        IExpirationsRoutingAdministrationService associations,
        IExpirationsBrokerConfigurationService brokers,
        Guid? initialBrokerId = null)
    {
        _associations = associations ?? throw new ArgumentNullException(nameof(associations));
        _brokers = brokers ?? throw new ArgumentNullException(nameof(brokers));
        _initialBrokerId = initialBrokerId;
        InitializeComponent();
        DataContext = State;
    }

    public bool HasSavedChanges { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!State.HasLoaded)
            await LoadAsync(selectInitialBroker: true);
    }

    private async Task LoadAsync(bool selectInitialBroker = false)
    {
        State.SetBusy(true, "Cargando asociaciones...");
        try
        {
            var associationsTask = _associations.ListKnownIdentifiersAsync();
            var brokersTask = _brokers.ListAsync();
            await Task.WhenAll(associationsTask, brokersTask);
            State.SetKnownData(
                await associationsTask,
                await brokersTask,
                selectInitialBroker ? _initialBrokerId : State.SelectedBrokerFilter?.BrokerId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible cargar la administración de asociaciones.\n\n{ex.Message}",
                "Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private async void AddAssociation_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedBroker is not { } broker)
            return;
        var editor = new ExpirationsAssociationEditorWindow(broker) { Owner = this };
        if (editor.ShowDialog() != true || editor.AssociationInput is not { } input)
            return;
        await RunMutationAsync(() => _associations.CreateAssociationAsync(
            broker.BrokerId,
            input.Kind,
            input.Value));
    }

    private async void EditAssociation_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedKnownIdentifier?.AssociationItem is not { } selected ||
            State.GetBroker(selected.Association.BrokerId) is not { } broker)
            return;
        var editor = new ExpirationsAssociationEditorWindow(broker, selected.Association) { Owner = this };
        if (editor.ShowDialog() != true || editor.AssociationInput is not { } input)
            return;
        await RunMutationAsync(() => _associations.EditAssociationAsync(
            selected.Association.Id,
            input.Kind,
            input.Value,
            selected.UpdateTime));
    }

    private async void DeactivateAssociation_Click(object sender, RoutedEventArgs e) =>
        await ChangeAssociationStateAsync(false);

    private async void ReactivateAssociation_Click(object sender, RoutedEventArgs e) =>
        await ChangeAssociationStateAsync(true);

    private async Task ChangeAssociationStateAsync(bool isActive)
    {
        if (State.SelectedKnownIdentifier?.AssociationItem is not { } selected)
            return;
        if (MessageBox.Show(
                $"¿Desea {(isActive ? "reactivar" : "inactivar")} la asociación '{selected.Association.Value}'?",
                "Asociaciones de vencimientos",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunMutationAsync(() => _associations.SetAssociationActiveAsync(
            selected.Association.Id,
            isActive,
            selected.UpdateTime));
    }

    private async void DeleteAssociation_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedKnownIdentifier?.AssociationItem is not { } selected)
            return;
        if (MessageBox.Show(
                "Esta asociación dejará de existir y no podrá utilizarse para identificar al corredor. " +
                "¿Desea continuar?",
                "Eliminar asociación",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunMutationAsync(() => _associations.DeleteAssociationAsync(
            selected.Association.Id,
            selected.UpdateTime));
    }

    private async void ReassignAssociation_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedKnownIdentifier is not { } known ||
            State.SelectedDestinationBroker is not { } destination)
            return;
        if (known.ObservedItem is { } observed)
        {
            var observedConfirmation =
                $"El valor \"{known.Value}\" se confirmará para {destination.DisplayText}.\n\n¿Desea continuar?";
            if (MessageBox.Show(
                    observedConfirmation,
                    "Reasignar y confirmar identificador",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            await RunMutationAsync(() => _associations.ReassignAndConfirmObservedIdentifierAsync(
                observed.Identifier.Id,
                destination.BrokerId,
                observed.UpdateTime));
            return;
        }
        if (known.AssociationItem is not { } selected)
            return;
        var confirmation =
            $"El valor \"{selected.Association.Value}\" dejará de estar asociado a " +
            $"{selected.BrokerName} y pasará a {destination.DisplayText}.\n\n¿Desea continuar?";
        if (MessageBox.Show(
                confirmation,
                "Reasignar asociación",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunMutationAsync(() => _associations.ReassignAsync(
            selected.Association.Id,
            destination.BrokerId,
            selected.UpdateTime));
    }

    private async void ConfirmObserved_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedKnownIdentifier?.ObservedItem is not { } observed)
            return;
        await RunMutationAsync(() => _associations.ConfirmObservedIdentifierAsync(
            observed.Identifier.Id,
            observed.UpdateTime));
    }

    private async void IgnoreObserved_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedKnownIdentifier?.ObservedItem is not { } observed)
            return;
        if (MessageBox.Show(
                $"¿Desea ignorar el identificador \"{observed.Identifier.Value}\"?",
                "Ignorar identificador",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunMutationAsync(() => _associations.IgnoreObservedIdentifierAsync(
            observed.Identifier.Id,
            observed.UpdateTime));
    }

    private async Task RunMutationAsync(Func<Task<ExpirationsRoutingAdministrationResult>> operation)
    {
        State.SetBusy(true, "Guardando cambio...");
        try
        {
            var result = await operation();
            HasSavedChanges |= result.WasPersisted;
            MessageBox.Show(
                result.Message,
                "Vencimientos",
                MessageBoxButton.OK,
                result.WasPersisted ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Vencimientos", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }
}

internal sealed record ExpirationsBrokerFilterOption(Guid? BrokerId, string DisplayText);

internal sealed class ExpirationsRoutingAdministrationState : INotifyPropertyChanged
{
    private readonly List<ExpirationsAssociationAdministrationItem> _allAssociations = [];
    private readonly List<ExpirationsKnownIdentifierAdministrationItem> _allKnownIdentifiers = [];
    private readonly List<ExpirationsBrokerConfigurationItem> _brokers = [];
    private string _associationSearchText = string.Empty;
    private ExpirationsBrokerFilterOption? _selectedBrokerFilter;
    private ExpirationsAssociationAdministrationItem? _selectedAssociation;
    private ExpirationsKnownIdentifierAdministrationItem? _selectedKnownIdentifier;
    private ExpirationsBrokerChoice? _selectedDestinationBroker;
    private bool _isBusy;
    private string _busyText = string.Empty;

    public ObservableCollection<ExpirationsAssociationAdministrationItem> VisibleAssociations { get; } = [];
    public ObservableCollection<ExpirationsKnownIdentifierAdministrationItem> VisibleKnownIdentifiers { get; } = [];
    public ObservableCollection<ExpirationsBrokerFilterOption> BrokerFilters { get; } = [];
    public ObservableCollection<ExpirationsBrokerChoice> DestinationBrokers { get; } = [];
    public bool HasLoaded { get; private set; }
    public bool IsUiEnabled => !_isBusy;
    public string BusyText => _busyText;
    public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;
    public ExpirationsBrokerConfigurationItem? SelectedBroker =>
        _brokers.FirstOrDefault(item => item.BrokerId == SelectedBrokerFilter?.BrokerId);
    public string SelectedBrokerName => SelectedBroker?.Name ?? string.Empty;
    public string SelectedBrokerEmail => SelectedBroker?.PrimaryEmailAddresses
        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "Sin correo principal";
    public string SelectedBrokerStatus => SelectedBroker?.StatusText ?? string.Empty;
    public Visibility BrokerIdentityVisibility => SelectedBroker is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoAssociationsVisibility => SelectedBroker is not null &&
        !_allKnownIdentifiers.Any(item => item.BrokerId == SelectedBroker.BrokerId && !item.IsMaster)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public bool CanAddAssociation => !_isBusy && SelectedBroker?.IsActive == true;
    public bool CanEditAssociation => !_isBusy && SelectedKnownIdentifier?.IsAssociation == true;
    public bool CanDeleteAssociation => !_isBusy && SelectedKnownIdentifier?.IsAssociation == true;
    public bool CanDeactivateAssociation => !_isBusy &&
        SelectedKnownIdentifier?.AssociationItem?.Association.IsActive == true;
    public bool CanReactivateAssociation => !_isBusy &&
        SelectedKnownIdentifier?.AssociationItem?.Association.IsActive == false;
    public bool CanConfirmObserved => !_isBusy && SelectedKnownIdentifier?.IsObserved == true;
    public bool CanIgnoreObserved => !_isBusy && SelectedKnownIdentifier?.IsObserved == true;
    public bool CanReassign => !_isBusy &&
        (SelectedKnownIdentifier?.IsAssociation == true || SelectedKnownIdentifier?.IsObserved == true) &&
        SelectedDestinationBroker is not null &&
        SelectedDestinationBroker.BrokerId != SelectedKnownIdentifier.BrokerId;
    public string ReassignButtonText => SelectedKnownIdentifier?.IsObserved == true
        ? "Reasignar y confirmar"
        : "Reasignar";

    public string AssociationSearchText
    {
        get => _associationSearchText;
        set { if (Set(ref _associationSearchText, value ?? string.Empty)) ApplyAssociationFilter(); }
    }

    public ExpirationsBrokerFilterOption? SelectedBrokerFilter
    {
        get => _selectedBrokerFilter;
        set
        {
            if (!Set(ref _selectedBrokerFilter, value)) return;
            NotifyBrokerIdentity();
            ApplyAssociationFilter();
        }
    }

    public ExpirationsAssociationAdministrationItem? SelectedAssociation
    {
        get => _selectedAssociation;
        set
        {
            if (!Set(ref _selectedAssociation, value)) return;
            NotifySelectionActions();
        }
    }

    public ExpirationsKnownIdentifierAdministrationItem? SelectedKnownIdentifier
    {
        get => _selectedKnownIdentifier;
        set
        {
            if (!Set(ref _selectedKnownIdentifier, value)) return;
            _selectedAssociation = value?.AssociationItem;
            Notify(nameof(SelectedAssociation));
            NotifySelectionActions();
        }
    }

    public ExpirationsBrokerChoice? SelectedDestinationBroker
    {
        get => _selectedDestinationBroker;
        set { if (Set(ref _selectedDestinationBroker, value)) Notify(nameof(CanReassign)); }
    }

    public ExpirationsBrokerConfigurationItem? GetBroker(Guid brokerId) =>
        _brokers.FirstOrDefault(item => item.BrokerId == brokerId);

    public void SetData(
        IEnumerable<ExpirationsAssociationAdministrationItem> associations,
        IEnumerable<ExpirationsBrokerConfigurationItem> brokers,
        Guid? selectedBrokerId)
    {
        var brokerItems = brokers.ToList();
        var associationItems = associations.ToList();
        var known = brokerItems.Select(broker => new ExpirationsKnownIdentifierAdministrationItem
            {
                BrokerId = broker.BrokerId,
                BrokerName = broker.Name,
                BrokerPrimaryEmail = broker.PrimaryEmailAddresses.FirstOrDefault() ?? string.Empty,
                Kind = ExpirationsAssociationKind.Name,
                Value = broker.Name,
                NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(broker.Name),
                OriginText = "Maestro",
                StatusText = broker.IsActive ? "Activo" : "Inactivo",
                UpdatedAtUtc = broker.ProfileUpdatedAtUtc,
                IsMaster = true
            })
            .Concat(associationItems.Select(item => new ExpirationsKnownIdentifierAdministrationItem
            {
                BrokerId = item.Association.BrokerId,
                BrokerName = item.BrokerName,
                BrokerPrimaryEmail = item.BrokerPrimaryEmail,
                Kind = item.Association.Kind,
                Value = item.Association.Value,
                NormalizedValue = item.Association.NormalizedValue,
                OriginText = item.OriginText,
                StatusText = item.Association.IsActive ? "Activo" : "Inactivo",
                UpdatedAtUtc = item.Association.UpdatedAtUtc,
                AssociationItem = item
            }));
        SetKnownData(known, brokerItems, selectedBrokerId);
    }

    public void SetKnownData(
        IEnumerable<ExpirationsKnownIdentifierAdministrationItem> knownIdentifiers,
        IEnumerable<ExpirationsBrokerConfigurationItem> brokers,
        Guid? selectedBrokerId)
    {
        var knownItems = knownIdentifiers.ToList();
        _allKnownIdentifiers.Clear();
        _allKnownIdentifiers.AddRange(knownItems
            .OrderBy(item => item.BrokerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(item => item.IsMaster)
            .ThenBy(item => item.Value, StringComparer.CurrentCultureIgnoreCase));
        _allAssociations.Clear();
        _allAssociations.AddRange(knownItems
            .Select(item => item.AssociationItem)
            .OfType<ExpirationsAssociationAdministrationItem>()
            .OrderBy(item => item.Association.Value, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Association.Id));
        _brokers.Clear();
        _brokers.AddRange(brokers
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId));
        BrokerFilters.Clear();
        BrokerFilters.Add(new ExpirationsBrokerFilterOption(null, "Todos los corredores"));
        DestinationBrokers.Clear();
        foreach (var broker in _brokers)
        {
            var email = broker.PrimaryEmailAddresses.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
            BrokerFilters.Add(new ExpirationsBrokerFilterOption(
                broker.BrokerId,
                email.Length == 0 ? broker.Name : $"{broker.Name} — {email}"));
            if (broker.IsActive)
                DestinationBrokers.Add(new ExpirationsBrokerChoice(broker.BrokerId, broker.Name, email));
        }
        _selectedBrokerFilter = BrokerFilters.FirstOrDefault(item => item.BrokerId == selectedBrokerId) ?? BrokerFilters[0];
        _selectedAssociation = null;
        _selectedKnownIdentifier = null;
        _selectedDestinationBroker = null;
        HasLoaded = true;
        ApplyAssociationFilter();
        Notify(string.Empty);
    }

    public void SetBusy(bool value, string? text = null)
    {
        _isBusy = value;
        if (text is not null) _busyText = text;
        Notify(string.Empty);
    }

    private void ApplyAssociationFilter()
    {
        var term = AssociationSearchText.Trim();
        var brokerId = SelectedBrokerFilter?.BrokerId;
        VisibleAssociations.Clear();
        VisibleKnownIdentifiers.Clear();
        foreach (var known in _allKnownIdentifiers.Where(item =>
                     (brokerId is null || item.BrokerId == brokerId) &&
                     (term.Length == 0 ||
                      item.Value.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                      item.KindText.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                      item.BrokerName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                      item.BrokerPrimaryEmail.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                      item.OriginText.Contains(term, StringComparison.CurrentCultureIgnoreCase))))
        {
            VisibleKnownIdentifiers.Add(known);
            if (known.AssociationItem is { } association)
                VisibleAssociations.Add(association);
        }
        SelectedKnownIdentifier = null;
        Notify(nameof(NoAssociationsVisibility));
    }

    private void NotifyBrokerIdentity()
    {
        Notify(nameof(SelectedBroker));
        Notify(nameof(SelectedBrokerName));
        Notify(nameof(SelectedBrokerEmail));
        Notify(nameof(SelectedBrokerStatus));
        Notify(nameof(BrokerIdentityVisibility));
        Notify(nameof(NoAssociationsVisibility));
        Notify(nameof(CanAddAssociation));
    }

    private void NotifySelectionActions()
    {
        Notify(nameof(CanEditAssociation));
        Notify(nameof(CanDeleteAssociation));
        Notify(nameof(CanDeactivateAssociation));
        Notify(nameof(CanReactivateAssociation));
        Notify(nameof(CanReassign));
        Notify(nameof(CanConfirmObserved));
        Notify(nameof(CanIgnoreObserved));
        Notify(nameof(ReassignButtonText));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(propertyName);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
