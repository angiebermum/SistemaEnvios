using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsWorkbookSelectionWindow : Window
{
    internal readonly ExpirationsWorkbookSelectionState State;

    public ExpirationsWorkbookSelectionWindow(ExpirationsWorkbookInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        State = new ExpirationsWorkbookSelectionState(inspection);
        InitializeComponent();
        DataContext = State;
    }

    public ExpirationsWorkbookReadOptions? Options { get; private set; }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (!State.TryCreateOptions(out var options))
        {
            MessageBox.Show(
                "Seleccione la hoja, la fila de encabezados y la columna que contiene el corredor.",
                "Selección incompleta",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Options = options;
        DialogResult = true;
    }
}

internal sealed class ExpirationsWorkbookSelectionState : INotifyPropertyChanged
{
    private ExpirationsWorksheetInspection? _selectedWorksheet;
    private ExpirationsHeaderRowInspection? _selectedHeaderRow;
    private ExpirationsColumnInspection? _selectedColumn;

    public ExpirationsWorkbookSelectionState(ExpirationsWorkbookInspection inspection)
    {
        Worksheets = inspection.Worksheets;
    }

    public IReadOnlyList<ExpirationsWorksheetInspection> Worksheets { get; }
    public IReadOnlyList<ExpirationsHeaderRowInspection> HeaderRows =>
        SelectedWorksheet?.HeaderRows ?? [];
    public IReadOnlyList<ExpirationsColumnInspection> Columns =>
        SelectedHeaderRow?.Columns ?? [];

    public ExpirationsWorksheetInspection? SelectedWorksheet
    {
        get => _selectedWorksheet;
        set
        {
            if (ReferenceEquals(_selectedWorksheet, value)) return;
            _selectedWorksheet = value;
            _selectedHeaderRow = null;
            _selectedColumn = null;
            Notify();
            Notify(nameof(HeaderRows));
            Notify(nameof(SelectedHeaderRow));
            Notify(nameof(Columns));
            Notify(nameof(SelectedColumn));
        }
    }

    public ExpirationsHeaderRowInspection? SelectedHeaderRow
    {
        get => _selectedHeaderRow;
        set
        {
            if (ReferenceEquals(_selectedHeaderRow, value)) return;
            _selectedHeaderRow = value;
            _selectedColumn = null;
            Notify();
            Notify(nameof(Columns));
            Notify(nameof(SelectedColumn));
        }
    }

    public ExpirationsColumnInspection? SelectedColumn
    {
        get => _selectedColumn;
        set
        {
            if (ReferenceEquals(_selectedColumn, value)) return;
            _selectedColumn = value;
            Notify();
        }
    }

    public bool TryCreateOptions(out ExpirationsWorkbookReadOptions options)
    {
        if (SelectedWorksheet is null || SelectedHeaderRow is null || SelectedColumn is null)
        {
            options = new ExpirationsWorkbookReadOptions();
            return false;
        }

        options = new ExpirationsWorkbookReadOptions
        {
            WorksheetName = SelectedWorksheet.WorksheetName,
            HeaderRowNumber = SelectedHeaderRow.RowNumber,
            BrokerColumnIndex = SelectedColumn.ColumnIndex
        };
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
