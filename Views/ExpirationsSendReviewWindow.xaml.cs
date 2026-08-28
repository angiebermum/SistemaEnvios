using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsSendReviewWindow : Window
{
    private readonly IExpirationsOutlookSender _sender;
    private readonly ExpirationsSendExecutionService _execution;
    internal readonly ExpirationsSendReviewState State;

    public ExpirationsSendReviewWindow(
        ExpirationsSendPreparationResult preparation,
        IExpirationsOutlookSender sender,
        IExpirationsSendHistoryRepository history)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        if (!preparation.CanSend)
            throw new ArgumentException("El preflight debe estar completo antes de abrir la revisión.", nameof(preparation));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _execution = new ExpirationsSendExecutionService(
            sender,
            history ?? throw new ArgumentNullException(nameof(history)));
        State = new ExpirationsSendReviewState(preparation);
        InitializeComponent();
        DataContext = State;
    }

    public ExpirationsSendExecutionResult? CompletedResult { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (State.OutlookChecked)
            return;
        State.SetBusy(true, "Comprobando Outlook...");
        try
        {
            State.ApplyOutlook(await _sender.CheckAvailabilityAsync());
        }
        catch (Exception ex)
        {
            State.ApplyOutlook(new OutlookConnectionInfo(false, ex.Message));
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private async void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (State.SelectedAccount is not { Length: > 0 } account)
            return;
        State.SetBusy(true, "Preparando envío...");
        try
        {
            var progress = new Progress<OutlookSendProgress>(value =>
                State.SetProgress($"Enviando correo {value.Current} de {value.Total}..."));
            var result = await _execution.SendAsync(
                State.Preparation,
                account,
                (count, selectedAccount) => MessageBox.Show(
                    $"Se enviarán {count} correos desde {selectedAccount}.\n¿Desea continuar?",
                    "Confirmar envío de Vencimientos",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question) == MessageBoxResult.Yes,
                progress);
            if (result.WasCancelled)
            {
                State.SetProgress("Envío cancelado. No se envió ningún correo.");
                return;
            }

            CompletedResult = result;
            State.ApplyResults(result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible completar el envío.\n\n{ex.Message}",
                "Envío de Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            State.SetBusy(false);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}

internal sealed class ExpirationsSendReviewState : INotifyPropertyChanged
{
    private bool _isBusy;
    private bool _outlookChecked;
    private bool _outlookAvailable;
    private string? _selectedAccount;
    private string _outlookStatusText = "Outlook aún no se ha comprobado.";
    private string _progressText = "Revise cada correo antes de continuar.";
    private ExpirationsSendExecutionResult? _result;

    public ExpirationsSendReviewState(ExpirationsSendPreparationResult preparation)
    {
        Preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        ReviewItems = new ObservableCollection<ExpirationsSendReviewItem>(
            preparation.Requests.Select(request => new ExpirationsSendReviewItem(request)));
    }

    public ExpirationsSendPreparationResult Preparation { get; }
    public ObservableCollection<ExpirationsSendReviewItem> ReviewItems { get; }
    public ObservableCollection<string> AvailableAccounts { get; } = [];
    public bool OutlookChecked => _outlookChecked;
    public string ProcessText => Preparation.Process switch
    {
        ExpirationsProcess.PreviousMonth => "Pendientes del mes anterior",
        ExpirationsProcess.NextMonth => "Vencimientos del mes siguiente",
        ExpirationsProcess.Cancellations => "Cancelaciones",
        _ => throw new ArgumentOutOfRangeException()
    };
    public string Subject => Preparation.Settings?.DefaultSubject ?? string.Empty;
    public string Message => Preparation.Settings?.DefaultMessage ?? string.Empty;
    public string WarningsText => string.Join(Environment.NewLine, Preparation.Warnings);
    public Visibility WarningsVisibility => Preparation.Warnings.Count > 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string OutlookStatusText => _outlookStatusText;
    public string ProgressText => _progressText;
    public string SummaryText => _result is null
        ? string.Empty
        : $"Enviados correctamente: {_result.SuccessfulCount} · Fallidos: {_result.FailedCount}" +
          (_result.UnknownCount > 0 ? $" · No confirmados: {_result.UnknownCount}" : string.Empty);
    public string HistoryWarningText => _result?.HistoryWarning ?? string.Empty;
    public Visibility HistoryWarningVisibility => HistoryWarningText.Length == 0
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility ResultsVisibility => _result is null ? Visibility.Collapsed : Visibility.Visible;
    public bool CanSelectAccount => !_isBusy && _outlookAvailable && AvailableAccounts.Count > 1 && _result is null;
    public bool CanContinue => !_isBusy && _outlookAvailable &&
        SelectedAccount is not null && AvailableAccounts.Contains(SelectedAccount) && _result is null;
    public bool CanCancel => !_isBusy;

    public string? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (string.Equals(_selectedAccount, value, StringComparison.OrdinalIgnoreCase))
                return;
            _selectedAccount = value;
            Notify();
            Notify(nameof(CanContinue));
        }
    }

    public void ApplyOutlook(OutlookConnectionInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        AvailableAccounts.Clear();
        foreach (var account in (info.AccountEmailAddresses ?? [])
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            AvailableAccounts.Add(account.Trim());
        }
        _outlookChecked = true;
        _outlookAvailable = info.Available && AvailableAccounts.Count > 0;
        _outlookStatusText = info.Available && AvailableAccounts.Count == 0
            ? "Outlook no devolvió ninguna cuenta de envío disponible."
            : info.Message;
        SelectedAccount = AvailableAccounts.FirstOrDefault(account =>
            account.Equals(info.EmailAddress, StringComparison.OrdinalIgnoreCase));
        if (SelectedAccount is null && AvailableAccounts.Count == 1)
            SelectedAccount = AvailableAccounts[0];
        Notify(string.Empty);
    }

    public void SetBusy(bool value, string? progress = null)
    {
        _isBusy = value;
        if (!string.IsNullOrWhiteSpace(progress))
            _progressText = progress;
        Notify(string.Empty);
    }

    public void SetProgress(string value)
    {
        _progressText = value;
        Notify(nameof(ProgressText));
    }

    public void ApplyResults(ExpirationsSendExecutionResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
        var byRequest = result.Items.ToDictionary(item => item.RequestId);
        foreach (var item in ReviewItems)
            item.ApplyResult(byRequest.GetValueOrDefault(item.RequestId));
        _progressText = "Envío finalizado.";
        Notify(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class ExpirationsSendReviewItem : INotifyPropertyChanged
{
    private string _resultText = string.Empty;

    public ExpirationsSendReviewItem(EmailSendRequest request)
    {
        RequestId = request.RequestId;
        BrokerName = request.BrokerName;
        ToText = string.Join("; ", request.ToRecipients);
        CcText = request.CcRecipients.Count == 0 ? "Sin CC" : string.Join("; ", request.CcRecipients);
        AttachmentNames = request.AttachmentPaths.Select(Path.GetFileName).ToList();
        ReviewText = request.RequiresReview
            ? string.IsNullOrWhiteSpace(request.ReviewNote)
                ? "Este correo requiere revisión."
                : request.ReviewNote
            : string.Empty;
    }

    public Guid RequestId { get; }
    public string BrokerName { get; }
    public string ToText { get; }
    public string CcText { get; }
    public IReadOnlyList<string?> AttachmentNames { get; }
    public string ReviewText { get; }
    public Visibility ReviewVisibility => ReviewText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string ResultText => _resultText;
    public Visibility ResultVisibility => _resultText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

    public void ApplyResult(ExpirationsSendResultItem? result)
    {
        if (result is null)
            return;
        _resultText = result.StatusText;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
