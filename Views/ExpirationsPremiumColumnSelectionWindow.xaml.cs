using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsPremiumColumnSelectionWindow : Window
{
    private readonly ExpirationsPremiumColumnSelectionState _state;

    public ExpirationsPremiumColumnSelectionWindow(
        ExpirationsWorkbookInspection inspection,
        string worksheetName,
        uint headerRowNumber,
        ExpirationsPremiumColumnOptions? currentOptions = null)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var worksheet = inspection.Worksheets.SingleOrDefault(item =>
            string.Equals(item.WorksheetName, worksheetName, StringComparison.Ordinal)) ??
            throw new InvalidOperationException($"No se encontró la hoja analizada '{worksheetName}'.");
        var header = worksheet.HeaderRows.SingleOrDefault(item => item.RowNumber == headerRowNumber) ??
            throw new InvalidOperationException($"No se encontró la fila de encabezado {headerRowNumber}.");
        _state = new ExpirationsPremiumColumnSelectionState(
            worksheetName,
            headerRowNumber,
            header.Columns,
            currentOptions);
        InitializeComponent();
        DataContext = _state;
    }

    public ExpirationsPremiumColumnOptions? Options { get; private set; }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        if (!_state.CanAccept)
            return;
        Options = new ExpirationsPremiumColumnOptions(
            _state.SelectedPremiumColumn!.ColumnIndex,
            _state.SelectedCurrencyColumn!.ColumnIndex);
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}

internal sealed class ExpirationsPremiumColumnSelectionState : INotifyPropertyChanged
{
    private ExpirationsColumnInspection? _selectedPremiumColumn;
    private ExpirationsColumnInspection? _selectedCurrencyColumn;

    public ExpirationsPremiumColumnSelectionState(
        string worksheetName,
        uint headerRowNumber,
        IReadOnlyList<ExpirationsColumnInspection> columns,
        ExpirationsPremiumColumnOptions? currentOptions)
    {
        WorkbookLocationText = $"Hoja: {worksheetName} · Encabezado: fila {headerRowNumber}";
        Columns = columns.OrderBy(column => column.ColumnIndex).ToArray();
        _selectedPremiumColumn = Columns.SingleOrDefault(column =>
            column.ColumnIndex == currentOptions?.PremiumColumnIndex);
        _selectedCurrencyColumn = Columns.SingleOrDefault(column =>
            column.ColumnIndex == currentOptions?.CurrencyColumnIndex);
    }

    public string WorkbookLocationText { get; }
    public IReadOnlyList<ExpirationsColumnInspection> Columns { get; }
    public bool CanAccept => SelectedPremiumColumn is not null &&
                             SelectedCurrencyColumn is not null &&
                             SelectedPremiumColumn.ColumnIndex != SelectedCurrencyColumn.ColumnIndex;

    public ExpirationsColumnInspection? SelectedPremiumColumn
    {
        get => _selectedPremiumColumn;
        set
        {
            if (ReferenceEquals(_selectedPremiumColumn, value)) return;
            _selectedPremiumColumn = value;
            Notify();
            Notify(nameof(CanAccept));
        }
    }

    public ExpirationsColumnInspection? SelectedCurrencyColumn
    {
        get => _selectedCurrencyColumn;
        set
        {
            if (ReferenceEquals(_selectedCurrencyColumn, value)) return;
            _selectedCurrencyColumn = value;
            Notify();
            Notify(nameof(CanAccept));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
