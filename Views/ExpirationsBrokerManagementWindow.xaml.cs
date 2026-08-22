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
    internal readonly ExpirationsBrokerManagementState State = new();

    public ExpirationsBrokerManagementWindow(IExpirationsBrokerConfigurationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        DataContext = State;
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
            State.SetItems(await _service.ListAsync());
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
        var editor = new ExpirationsBrokerProfileWindow(_service, selected) { Owner = this };
        if (editor.ShowDialog() != true || editor.SavedConfiguration is null)
            return;
        State.Replace(editor.SavedConfiguration);
        HasSavedChanges |= editor.WasPersisted;
    }

    private void ToggleInactive_Click(object sender, RoutedEventArgs e) => State.ToggleInactiveVisibility();
}

internal sealed class ExpirationsBrokerManagementState : INotifyPropertyChanged
{
    private readonly List<ExpirationsBrokerConfigurationItem> _allItems = [];
    private string _searchText = string.Empty;
    private ExpirationsBrokerConfigurationItem? _selectedItem;
    private bool _isBusy;
    private string _busyText = string.Empty;
    private bool _showInactive;

    public ObservableCollection<ExpirationsBrokerConfigurationItem> VisibleItems { get; } = [];
    public bool HasLoaded { get; private set; }
    public bool IsBusy => _isBusy;
    public bool IsUiEnabled => !IsBusy;
    public string BusyText => _busyText;
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool CanConfigure => !IsBusy && SelectedItem is not null;
    public int InactiveCount => _allItems.Count(item => !item.IsActive);
    public bool ShowInactive => _showInactive;
    public string InactiveButtonText => ShowInactive
        ? "Ocultar inactivos"
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
        }
    }

    public void SetItems(IEnumerable<ExpirationsBrokerConfigurationItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _allItems.Clear();
        _allItems.AddRange(items
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId));
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

    public void Replace(ExpirationsBrokerConfigurationItem item)
    {
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
    }

    private void ApplyFilter()
    {
        var selectedId = SelectedItem?.BrokerId;
        var term = SearchText.Trim();
        var filtered = _allItems.Where(item => (item.IsActive || ShowInactive) &&
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
