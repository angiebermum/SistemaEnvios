using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsExclusionsWindow : Window
{
    private readonly IExpirationsRoutingAdministrationService _service;
    internal readonly ExpirationsExclusionsState State = new();

    public ExpirationsExclusionsWindow(IExpirationsRoutingAdministrationService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        InitializeComponent();
        DataContext = State;
    }

    public bool HasSavedChanges { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!State.HasLoaded)
            await LoadAsync();
    }

    private async Task LoadAsync()
    {
        State.SetBusy(true, "Cargando exclusiones...");
        try
        {
            State.SetItems(await _service.ListExclusionsAsync());
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible cargar las exclusiones.\n\n{ex.Message}",
                "Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private async void Deactivate_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(false);
    private async void Reactivate_Click(object sender, RoutedEventArgs e) => await ChangeStateAsync(true);

    private async Task ChangeStateAsync(bool isActive)
    {
        if (State.SelectedItem is not { } selected)
            return;
        if (MessageBox.Show(
                $"¿Desea {(isActive ? "reactivar" : "desactivar")} la exclusión '{selected.Exclusion.Value}'?",
                "Exclusiones de vencimientos",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        State.SetBusy(true, "Guardando cambio...");
        try
        {
            var result = await _service.SetExclusionActiveAsync(
                selected.Exclusion.Id,
                isActive,
                selected.UpdateTime);
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

internal sealed class ExpirationsExclusionsState : INotifyPropertyChanged
{
    private readonly List<ExpirationsExclusionAdministrationItem> _allItems = [];
    private string _searchText = string.Empty;
    private ExpirationsExclusionAdministrationItem? _selectedItem;
    private bool _isBusy;
    private string _busyText = string.Empty;

    public ObservableCollection<ExpirationsExclusionAdministrationItem> VisibleItems { get; } = [];
    public bool HasLoaded { get; private set; }
    public bool IsUiEnabled => !_isBusy;
    public string BusyText => _busyText;
    public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool CanDeactivate => !_isBusy && SelectedItem?.Exclusion.IsActive == true;
    public bool CanReactivate => !_isBusy && SelectedItem?.Exclusion.IsActive == false;

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

    public ExpirationsExclusionAdministrationItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value)) return;
            _selectedItem = value;
            Notify();
            Notify(nameof(CanDeactivate));
            Notify(nameof(CanReactivate));
        }
    }

    public void SetItems(IEnumerable<ExpirationsExclusionAdministrationItem> items)
    {
        _allItems.Clear();
        _allItems.AddRange(items
            .OrderBy(item => item.Exclusion.Value, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Exclusion.Id));
        HasLoaded = true;
        ApplyFilter();
    }

    public void SetBusy(bool value, string? text = null)
    {
        _isBusy = value;
        if (text is not null) _busyText = text;
        Notify(string.Empty);
    }

    private void ApplyFilter()
    {
        var term = SearchText.Trim();
        VisibleItems.Clear();
        foreach (var item in _allItems.Where(item => term.Length == 0 ||
                     item.Exclusion.Value.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            VisibleItems.Add(item);
        SelectedItem = null;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
