using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsSendHistoryWindow : Window
{
    private readonly IExpirationsSendHistoryRepository _history;
    private readonly ExpirationsRetryPreparationService _retryPreparation;
    private readonly ExpirationsGenerationBatch? _currentBatch;
    private readonly IExpirationsOutlookSender _outlookSender;
    internal ExpirationsSendHistoryState State { get; }

    public ExpirationsSendHistoryWindow(
        IExpirationsSendHistoryRepository history,
        ExpirationsRetryPreparationService retryPreparation,
        ExpirationsGenerationBatch? currentBatch,
        IExpirationsOutlookSender outlookSender)
    {
        _history = history ?? throw new ArgumentNullException(nameof(history));
        _retryPreparation = retryPreparation ?? throw new ArgumentNullException(nameof(retryPreparation));
        _currentBatch = currentBatch;
        _outlookSender = outlookSender ?? throw new ArgumentNullException(nameof(outlookSender));
        State = new ExpirationsSendHistoryState();
        InitializeComponent();
        DataContext = State;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadOperationsAsync();

    private async void Operations_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        State.ClearItems();
        if (State.SelectedOperation is { } selected)
            await LoadItemsAsync(selected.Operation);
    }

    private async Task LoadOperationsAsync(Guid? selectOperationId = null)
    {
        State.SetBusy(true, "Cargando historial de Vencimientos...");
        try
        {
            var operations = await _history.ListOperationsAsync();
            State.ApplyOperations(operations.Select(document => document.Value), selectOperationId);
        }
        catch (Exception ex)
        {
            State.ApplyLoadError($"No fue posible cargar el historial: {ex.Message}");
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private async Task LoadItemsAsync(ExpirationsSendOperation operation)
    {
        State.SetBusy(true, "Cargando detalle de la operación...");
        try
        {
            var items = await _history.ListItemsAsync(operation.OperationId);
            var values = items.Select(document => document.Value)
                .OrderBy(item => item.BrokerName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.ItemId)
                .ToList();
            State.ApplyItems(
                values,
                _retryPreparation.Prepare(operation, values, _currentBatch));
        }
        catch (Exception ex)
        {
            State.ApplyLoadError($"No fue posible cargar el detalle: {ex.Message}");
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private async void RetryFailed_Click(object sender, RoutedEventArgs e)
    {
        if (State.RetryPreparation?.Preparation is not { CanSend: true } preparation)
            return;
        var review = new ExpirationsSendReviewWindow(preparation, _outlookSender, _history)
        {
            Owner = this
        };
        _ = review.ShowDialog();
        if (review.CompletedResult?.OperationId is { } operationId)
            await LoadOperationsAsync(operationId);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class ExpirationsSendHistoryState : INotifyPropertyChanged
{
    private ExpirationsSendHistoryOperationRow? _selectedOperation;
    private ExpirationsRetryPreparationResult? _retryPreparation;
    private bool _isBusy;
    private string _statusText = "Seleccione una operación para ver sus correos.";

    public ObservableCollection<ExpirationsSendHistoryOperationRow> Operations { get; } = [];
    public ObservableCollection<ExpirationsSendHistoryItemRow> Items { get; } = [];
    public ExpirationsRetryPreparationResult? RetryPreparation => _retryPreparation;
    public bool CanRetry => !_isBusy && _retryPreparation?.CanRetry == true;
    public bool CanClose => !_isBusy;
    public string RetryStatusText => _statusText;

    public ExpirationsSendHistoryOperationRow? SelectedOperation
    {
        get => _selectedOperation;
        set
        {
            if (ReferenceEquals(_selectedOperation, value))
                return;
            _selectedOperation = value;
            Notify();
        }
    }

    public void ApplyOperations(
        IEnumerable<ExpirationsSendOperation> operations,
        Guid? selectOperationId = null)
    {
        Operations.Clear();
        foreach (var operation in operations
                     .OrderByDescending(value => value.StartedAtUtc)
                     .ThenByDescending(value => value.OperationId))
        {
            Operations.Add(new ExpirationsSendHistoryOperationRow(operation));
        }
        SelectedOperation = selectOperationId is { } id
            ? Operations.FirstOrDefault(row => row.Operation.OperationId == id) ?? Operations.FirstOrDefault()
            : Operations.FirstOrDefault();
        if (Operations.Count == 0)
        {
            ClearItems();
            _statusText = "Todavía no hay operaciones de envío de Vencimientos.";
        }
        Notify(string.Empty);
    }

    public void ApplyItems(
        IReadOnlyList<ExpirationsSendHistoryItem> items,
        ExpirationsRetryPreparationResult retryPreparation)
    {
        Items.Clear();
        foreach (var item in items)
            Items.Add(new ExpirationsSendHistoryItemRow(item));
        _retryPreparation = retryPreparation;
        _statusText = retryPreparation.CanRetry
            ? $"Listo para reintentar únicamente {items.Count(item => item.Status == ExpirationsSendItemStatus.Failed)} correo(s) fallido(s)."
            : string.Join(Environment.NewLine, retryPreparation.Errors);
        Notify(string.Empty);
    }

    public void ClearItems()
    {
        Items.Clear();
        _retryPreparation = null;
        _statusText = "Seleccione una operación para ver sus correos.";
        Notify(string.Empty);
    }

    public void ApplyLoadError(string message)
    {
        Items.Clear();
        _retryPreparation = null;
        _statusText = message;
        Notify(string.Empty);
    }

    public void SetBusy(bool value, string? status = null)
    {
        _isBusy = value;
        if (!string.IsNullOrWhiteSpace(status))
            _statusText = status;
        Notify(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class ExpirationsSendHistoryOperationRow(ExpirationsSendOperation operation)
{
    public ExpirationsSendOperation Operation { get; } = operation;
    public string DateText => Operation.StartedAtUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    public string ProcessText => Operation.Process == ExpirationsProcess.PreviousMonth
        ? "Mes anterior"
        : "Mes siguiente";
    public string Account => Operation.SendingAccountEmail;
    public int Total => Operation.TotalCount;
    public int Successes => Operation.SuccessCount;
    public int Failures => Operation.FailureCount;
    public string StatusText => Operation.Status == ExpirationsSendOperationStatus.Completed
        ? "Completada"
        : "Incompleta / no confirmado";
}

internal sealed class ExpirationsSendHistoryItemRow(ExpirationsSendHistoryItem item)
{
    public string BrokerName => item.BrokerName;
    public string ToText => string.Join("; ", item.ToRecipients);
    public string FilesText => string.Join(Environment.NewLine, item.Attachments.Select(value => value.FileName));
    public string StatusText => item.Status switch
    {
        ExpirationsSendItemStatus.Succeeded => "Exitoso",
        ExpirationsSendItemStatus.Failed => "Fallido",
        _ => "Pendiente / desconocido"
    };
    public string ErrorText => item.ErrorMessage;
}
