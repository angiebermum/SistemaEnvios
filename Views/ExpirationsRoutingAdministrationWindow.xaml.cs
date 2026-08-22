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
    private readonly IExpirationsRoutingAdministrationService _routing;
    private readonly IExpirationsBrokerConfigurationService _brokers;
    private readonly Guid? _initialBrokerId;
    internal readonly ExpirationsRoutingAdministrationState State = new();

    public ExpirationsRoutingAdministrationWindow(
        IExpirationsRoutingAdministrationService routing,
        IExpirationsBrokerConfigurationService brokers,
        Guid? initialBrokerId = null)
    {
        _routing = routing ?? throw new ArgumentNullException(nameof(routing));
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
        State.SetBusy(true, "Cargando asociaciones y exclusiones...");
        try
        {
            var associationsTask = _routing.ListAssociationsAsync();
            var exclusionsTask = _routing.ListExclusionsAsync();
            var brokersTask = _brokers.ListAsync();
            await Task.WhenAll(associationsTask, exclusionsTask, brokersTask);
            State.SetData(
                await associationsTask,
                await exclusionsTask,
                await brokersTask,
                selectInitialBroker ? _initialBrokerId : State.SelectedBrokerFilter?.BrokerId);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible cargar la administración de routing.\n\n{ex.Message}",
                "Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private async void DeactivateAssociation_Click(object sender, RoutedEventArgs e) =>
        await ChangeAssociationStateAsync(false);

    private async void ReactivateAssociation_Click(object sender, RoutedEventArgs e) =>
        await ChangeAssociationStateAsync(true);

    private async Task ChangeAssociationStateAsync(bool isActive)
    {
        if (State.SelectedAssociation is not { } selected)
            return;
        if (MessageBox.Show(
                $"¿Desea {(isActive ? "reactivar" : "desactivar")} la asociación '{selected.Association.Value}'?",
                "Asociaciones de Vencimientos",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunMutationAsync(() => _routing.SetAssociationActiveAsync(
            selected.Association.Id,
            isActive,
            selected.UpdateTime));
    }

    private async void ReassignAssociation_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedAssociation is not { } selected ||
            State.SelectedDestinationBroker is not { } destination)
            return;
        var confirmation =
            $"El valor {selected.Association.Value} dejará de estar asociado a " +
            $"{selected.BrokerName} y pasará a {destination.DisplayText}.\n\n¿Desea continuar?";
        if (MessageBox.Show(
                confirmation,
                "Reasignar asociación",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        await RunMutationAsync(() => _routing.ReassignAsync(
            selected.Association.Id,
            destination.BrokerId,
            selected.UpdateTime));
    }

    private async void DeactivateExclusion_Click(object sender, RoutedEventArgs e) =>
        await ChangeExclusionStateAsync(false);

    private async void ReactivateExclusion_Click(object sender, RoutedEventArgs e) =>
        await ChangeExclusionStateAsync(true);

    private async Task ChangeExclusionStateAsync(bool isActive)
    {
        if (State.SelectedExclusion is not { } selected)
            return;
        if (MessageBox.Show(
                $"¿Desea {(isActive ? "reactivar" : "desactivar")} la exclusión '{selected.Exclusion.Value}'?",
                "No corresponde distribución",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        await RunMutationAsync(() => _routing.SetExclusionActiveAsync(
            selected.Exclusion.Id,
            isActive,
            selected.UpdateTime));
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
    private readonly List<ExpirationsExclusionAdministrationItem> _allExclusions = [];
    private string _associationSearchText = string.Empty;
    private string _exclusionSearchText = string.Empty;
    private ExpirationsBrokerFilterOption? _selectedBrokerFilter;
    private ExpirationsAssociationAdministrationItem? _selectedAssociation;
    private ExpirationsExclusionAdministrationItem? _selectedExclusion;
    private ExpirationsBrokerChoice? _selectedDestinationBroker;
    private bool _isBusy;
    private string _busyText = string.Empty;

    public ObservableCollection<ExpirationsAssociationAdministrationItem> VisibleAssociations { get; } = [];
    public ObservableCollection<ExpirationsExclusionAdministrationItem> VisibleExclusions { get; } = [];
    public ObservableCollection<ExpirationsBrokerFilterOption> BrokerFilters { get; } = [];
    public ObservableCollection<ExpirationsBrokerChoice> DestinationBrokers { get; } = [];
    public bool HasLoaded { get; private set; }
    public bool IsUiEnabled => !_isBusy;
    public string BusyText => _busyText;
    public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool CanDeactivateAssociation => !_isBusy && SelectedAssociation?.Association.IsActive == true;
    public bool CanReactivateAssociation => !_isBusy && SelectedAssociation?.Association.IsActive == false;
    public bool CanReassign => !_isBusy && SelectedAssociation is not null && SelectedDestinationBroker is not null;
    public bool CanDeactivateExclusion => !_isBusy && SelectedExclusion?.Exclusion.IsActive == true;
    public bool CanReactivateExclusion => !_isBusy && SelectedExclusion?.Exclusion.IsActive == false;

    public string AssociationSearchText
    {
        get => _associationSearchText;
        set { if (Set(ref _associationSearchText, value ?? string.Empty)) ApplyAssociationFilter(); }
    }

    public string ExclusionSearchText
    {
        get => _exclusionSearchText;
        set { if (Set(ref _exclusionSearchText, value ?? string.Empty)) ApplyExclusionFilter(); }
    }

    public ExpirationsBrokerFilterOption? SelectedBrokerFilter
    {
        get => _selectedBrokerFilter;
        set { if (Set(ref _selectedBrokerFilter, value)) ApplyAssociationFilter(); }
    }

    public ExpirationsAssociationAdministrationItem? SelectedAssociation
    {
        get => _selectedAssociation;
        set
        {
            if (!Set(ref _selectedAssociation, value)) return;
            Notify(nameof(CanDeactivateAssociation));
            Notify(nameof(CanReactivateAssociation));
            Notify(nameof(CanReassign));
        }
    }

    public ExpirationsExclusionAdministrationItem? SelectedExclusion
    {
        get => _selectedExclusion;
        set
        {
            if (!Set(ref _selectedExclusion, value)) return;
            Notify(nameof(CanDeactivateExclusion));
            Notify(nameof(CanReactivateExclusion));
        }
    }

    public ExpirationsBrokerChoice? SelectedDestinationBroker
    {
        get => _selectedDestinationBroker;
        set { if (Set(ref _selectedDestinationBroker, value)) Notify(nameof(CanReassign)); }
    }

    public void SetData(
        IEnumerable<ExpirationsAssociationAdministrationItem> associations,
        IEnumerable<ExpirationsExclusionAdministrationItem> exclusions,
        IEnumerable<ExpirationsBrokerConfigurationItem> brokers,
        Guid? selectedBrokerId)
    {
        _allAssociations.Clear();
        _allAssociations.AddRange(associations);
        _allExclusions.Clear();
        _allExclusions.AddRange(exclusions);
        var brokerList = brokers.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        BrokerFilters.Clear();
        BrokerFilters.Add(new ExpirationsBrokerFilterOption(null, "Todos los corredores"));
        DestinationBrokers.Clear();
        foreach (var broker in brokerList)
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
        _selectedExclusion = null;
        _selectedDestinationBroker = null;
        HasLoaded = true;
        ApplyAssociationFilter();
        ApplyExclusionFilter();
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
        foreach (var item in _allAssociations.Where(item =>
                     (brokerId is null || item.Association.BrokerId == brokerId) &&
                     (term.Length == 0 ||
                      item.Association.Value.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                      item.KindText.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                      item.BrokerName.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
                      item.BrokerPrimaryEmail.Contains(term, StringComparison.OrdinalIgnoreCase))))
            VisibleAssociations.Add(item);
        SelectedAssociation = null;
    }

    private void ApplyExclusionFilter()
    {
        var term = ExclusionSearchText.Trim();
        VisibleExclusions.Clear();
        foreach (var item in _allExclusions.Where(item =>
                     term.Length == 0 ||
                     item.Exclusion.Value.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            VisibleExclusions.Add(item);
        SelectedExclusion = null;
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
