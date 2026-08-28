using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsBrokerManagementWindow : Window
{
    private readonly IExpirationsBrokerConfigurationService _service;
    private readonly IExpirationsRoutingAdministrationService? _routingAdministration;
    private readonly ExpirationsProcess _process;
    internal readonly ExpirationsBrokerManagementState State;

    public ExpirationsBrokerManagementWindow(
        IExpirationsBrokerConfigurationService service,
        IExpirationsRoutingAdministrationService? routingAdministration = null,
        ExpirationsProcess process = ExpirationsProcess.NextMonth)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _routingAdministration = routingAdministration;
        _process = process;
        State = new ExpirationsBrokerManagementState(process);
        InitializeComponent();
        DataContext = State;
        RoutingAdministrationButton.Visibility = routingAdministration is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        ViewExclusionsButton.Visibility = routingAdministration is null
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public bool HasSavedChanges { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (State.HasLoaded)
            return;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        State.SetBusy(true, "Cargando corredores...");
        try
        {
            var brokersTask = _service.ListAsync();
            var exclusionsTask = _routingAdministration?.ListExclusionsAsync();
            if (exclusionsTask is not null)
                await Task.WhenAll(brokersTask, exclusionsTask);
            State.SetItems(await brokersTask);
            if (exclusionsTask is not null)
                State.SetExclusionCount((await exclusionsTask).Count(item => item.Exclusion.IsActive));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible cargar la configuración de corredores.\n\n{ex.Message}",
                "Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private void ConfigureSelected_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedItem is not { } selected)
            return;
        var editor = new ExpirationsBrokerProfileWindow(_service, selected, _process) { Owner = this };
        if (editor.ShowDialog() != true || editor.SavedConfiguration is null)
            return;
        State.Replace(editor.SavedConfiguration);
        HasSavedChanges |= editor.WasPersisted;
    }

    private void ToggleInactive_Click(object sender, RoutedEventArgs e) => State.ToggleInactiveVisibility();

    private async void ViewExclusions_Click(object sender, RoutedEventArgs e)
    {
        if (_routingAdministration is null)
            return;
        var window = new ExpirationsExclusionsWindow(_routingAdministration) { Owner = this };
        _ = window.ShowDialog();
        HasSavedChanges |= window.HasSavedChanges;
        if (!window.HasSavedChanges)
            return;
        try
        {
            var exclusions = await _routingAdministration.ListExclusionsAsync();
            State.SetExclusionCount(exclusions.Count(item => item.Exclusion.IsActive));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Vencimientos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RoutingAdministration_Click(object sender, RoutedEventArgs e)
    {
        if (_routingAdministration is null)
            return;
        var window = new ExpirationsRoutingAdministrationWindow(
            _routingAdministration,
            _service,
            State.SelectedItem?.BrokerId)
        {
            Owner = this
        };
        _ = window.ShowDialog();
        HasSavedChanges |= window.HasSavedChanges;
    }
}

internal sealed class ExpirationsBrokerManagementState : INotifyPropertyChanged
{
    private readonly List<ExpirationsBrokerConfigurationItem> _allItems = [];
    private readonly ExpirationsProcess _process;
    private string _searchText = string.Empty;
    private ExpirationsBrokerConfigurationItem? _selectedItem;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private bool _showInactive;

    public ExpirationsBrokerManagementState(
        ExpirationsProcess process = ExpirationsProcess.PreviousMonth) =>
        _process = process;

    public ObservableCollection<ExpirationsBrokerConfigurationItem> VisibleItems { get; } = [];
    public bool HasLoaded { get; private set; }
    public bool IsBusy => _isBusy;
    public bool IsUiEnabled => !IsBusy;
    public string BusyText => _busyText;
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool CanConfigure => !IsBusy && SelectedItem is not null;
    public bool CanAdministerAssociations => !IsBusy && SelectedItem is not null;
    public int InactiveCount => _allItems.Count(item => !item.IsActive);
    public int ExclusionCount { get; private set; }
    public string ExclusionsButtonText => $"Ver excluidos ({ExclusionCount})";
    public bool ShowInactive => _showInactive;
    public string InactiveButtonText => ShowInactive
        ? "Ver todos"
        : $"Ver inactivos ({InactiveCount})";

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal)) return;
            _searchText = value ?? string.Empty;
            Notify();
            ApplyFilter();
        }
    }

    public ExpirationsBrokerConfigurationItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            _selectedItem = value;
            Notify();
            Notify(nameof(CanConfigure));
            Notify(nameof(CanAdministerAssociations));
        }
    }

    public void SetItems(IEnumerable<ExpirationsBrokerConfigurationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _allItems.Clear();
        var orderedItems = items
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId)
            .ToList();
        foreach (var item in orderedItems)
            item.AssistantSummaryProcess = _process;
        _allItems.AddRange(orderedItems);
        HasLoaded = true;
        SelectedItem = null;
        ApplyFilter();
    }

    public void ToggleInactiveVisibility()
    {
        _showInactive = !_showInactive;
        Notify(nameof(ShowInactive));
        Notify(nameof(InactiveButtonText));
        ApplyFilter();
    }

    public void SetExclusionCount(int value)
    {
        ExclusionCount = Math.Max(0, value);
        Notify(nameof(ExclusionCount));
        Notify(nameof(ExclusionsButtonText));
    }

    public void Replace(ExpirationsBrokerConfigurationItem item)
    {
        item.AssistantSummaryProcess = _process;
        var index = _allItems.FindIndex(value => value.BrokerId == item.BrokerId);
        if (index >= 0)
            _allItems[index] = item;
        else
            _allItems.Add(item);
        _allItems.Sort((left, right) =>
        {
            var name = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            return name != 0 ? name : left.BrokerId.CompareTo(right.BrokerId);
        });
        ApplyFilter();
        SelectedItem = VisibleItems.FirstOrDefault(value => value.BrokerId == item.BrokerId);
    }

    public void SetBusy(bool value, string? text = null)
    {
        _isBusy = value;
        if (text is not null)
            _busyText = text;
        Notify(nameof(IsBusy));
        Notify(nameof(IsUiEnabled));
        Notify(nameof(BusyText));
        Notify(nameof(BusyVisibility));
        Notify(nameof(CanConfigure));
        Notify(nameof(CanAdministerAssociations));
    }

    private void ApplyFilter()
    {
        var selectedId = SelectedItem?.BrokerId;
        var term = SearchText.Trim();
        var filtered = _allItems.Where(item => (ShowInactive ? !item.IsActive : item.IsActive) &&
            (term.Length == 0 ||
             item.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) ||
             item.PrimaryEmailAddresses.Any(email =>
                 email.Contains(term, StringComparison.OrdinalIgnoreCase))));
        VisibleItems.Clear();
        foreach (var item in filtered)
            VisibleItems.Add(item);
        SelectedItem = selectedId is null
            ? null
            : VisibleItems.FirstOrDefault(item => item.BrokerId == selectedId.Value);
        Notify(nameof(InactiveCount));
        Notify(nameof(InactiveButtonText));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
