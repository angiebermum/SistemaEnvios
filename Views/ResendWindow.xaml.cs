using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;
using Microsoft.Win32;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ResendWindow : Window
{
    private readonly ObservableCollection<SentEmailRecord> _records;
    private readonly string _currentCcText;
    private readonly AppConfiguration _configuration;
    private readonly EmailValidationService _validationService;
    private readonly OutlookEmailService _outlookService;
    private readonly AttachmentArchiveService _archiveService;
    private readonly IRuntimeDataService _runtimeData;
    private readonly FileLogger _logger;
    private readonly ObservableCollection<string> _attachments = [];
    private bool _isBusy;

    public ResendWindow(
        ObservableCollection<SentEmailRecord> records,
        SentEmailRecord? initialRecord,
        string currentCcText,
        AppConfiguration configuration,
        EmailValidationService validationService,
        OutlookEmailService outlookService,
        AttachmentArchiveService archiveService,
        IRuntimeDataService runtimeData,
        FileLogger logger)
    {
        InitializeComponent();
        _records = records;
        _currentCcText = currentCcText;
        _configuration = configuration;
        _validationService = validationService;
        _outlookService = outlookService;
        _archiveService = archiveService;
        _runtimeData = runtimeData;
        _logger = logger;

        RecordsComboBox.ItemsSource = _records;
        AttachmentsList.ItemsSource = _attachments;
        RecordsComboBox.SelectedItem = initialRecord ?? _records.FirstOrDefault();
    }

    public SentEmailRecord? NewRecord { get; private set; }

    private SentEmailRecord? SelectedRecord => RecordsComboBox.SelectedItem as SentEmailRecord;

    private void Record_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isBusy || SelectedRecord is not { } record)
        {
            return;
        }

        var broker = _configuration.Brokers.FirstOrDefault(value => value.Id == record.BrokerId);
        BrokerTextBox.Text = broker?.IdentityText ?? record.BrokerIdentityText;
        var recipients = ResolveRecipients(record, broker, _currentCcText);
        ToTextBox.Text = string.Join("; ", recipients.ToRecipients);
        CcTextBox.Text = string.Join("; ", recipients.CcRecipients);
        SubjectTextBox.Text = record.Subject;
        _attachments.Clear();
        foreach (var path in record.ArchivedAttachmentPaths.Where(File.Exists))
        {
            _attachments.Add(path);
        }

        StatusTextBlock.Text = recipients.Errors.Count > 0
            ? string.Join(" ", recipients.Errors)
            : record.ArchivedAttachmentPaths.Count > _attachments.Count
            ? "Algunos archivos archivados ya no existen. Agregue los reemplazos antes de reenviar."
            : "Puede cambiar el asunto y reemplazar uno o más archivos.";
    }

    private void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Agregar archivos de reemplazo",
            Filter = "Archivos de Excel (*.xlsx;*.xls)|*.xlsx;*.xls",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var existing = new HashSet<string>(_attachments.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        foreach (var selectedPath in dialog.FileNames)
        {
            var fullPath = Path.GetFullPath(selectedPath);
            if (EmailValidationService.IsAllowedExcelFile(fullPath) && existing.Add(fullPath))
            {
                _attachments.Add(fullPath);
            }
        }
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is string path)
        {
            _attachments.Remove(path);
        }
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || _records.Count == 0)
        {
            return;
        }

        if (MessageBox.Show(
                $"¿Desea eliminar los {_records.Count} registro(s) del historial?\n\n" +
                "Los archivos archivados no serán eliminados.",
                "Limpiar historial", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _runtimeData.SaveRecentSendsAsync([]);
            RecordsComboBox.SelectedItem = null;
            _records.Clear();
            _attachments.Clear();
            MessageBox.Show("El historial de envíos recientes fue eliminado.",
                "Historial eliminado", MessageBoxButton.OK, MessageBoxImage.Information);
            Close();
        }
        catch (Exception ex)
        {
            _logger.Error("No fue posible limpiar el historial de envíos recientes.", ex);
            MessageBox.Show($"No fue posible limpiar el historial.\n\n{ex.Message}",
                "Error al limpiar", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Resend_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || SelectedRecord is not { } originalRecord)
        {
            return;
        }

        var broker = _configuration.Brokers.FirstOrDefault(value => value.Id == originalRecord.BrokerId);
        var resolved = ResolveRecipients(originalRecord, broker, CcTextBox.Text);
        var toAddresses = resolved.ToRecipients;
        _validationService.TryParseAddresses(CcTextBox.Text, false, out var ccAddresses, out var ccErrors);
        var toSet = new HashSet<string>(toAddresses, StringComparer.OrdinalIgnoreCase);
        ccAddresses = ccAddresses.Where(address => !toSet.Contains(address)).ToList();
        var requiresReview = broker?.RequiresReview == true;
        var reviewConfirmed = false;
        if (requiresReview)
        {
            if (MessageBox.Show(
                    $"'{broker!.IdentityText}' requiere revisión.\n\n{broker.ReviewNote}\n\n¿Confirma explícitamente que desea reenviar?",
                    "Revisión requerida", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            reviewConfirmed = true;
        }

        var request = new EmailSendRequest
        {
            BrokerId = originalRecord.BrokerId,
            BrokerName = originalRecord.BrokerName,
            BrokerPrimaryRecipients = resolved.BrokerPrimaryRecipients,
            AssistantRecipients = resolved.AssistantRecipients,
            ToRecipients = toAddresses,
            CcRecipients = ccAddresses,
            Subject = SubjectTextBox.Text.Trim(),
            Body = originalRecord.Body,
            AttachmentPaths = _attachments.ToList(),
            SignatureImagePath = _configuration.SignatureImagePath,
            RequiresReview = requiresReview,
            ReviewNote = broker?.ReviewNote ?? string.Empty,
            ReviewConfirmed = reviewConfirmed,
            ResendOfRecordId = originalRecord.Id,
            PaymentGenerationId = originalRecord.PaymentGenerationId
        };
        var errors = new List<string>();
        errors.AddRange(resolved.Errors);
        errors.AddRange(ccErrors.Select(error => $"CC: {error}"));
        errors.AddRange(_validationService.ValidateRequest(request));
        errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (errors.Count > 0)
        {
            MessageBox.Show("No se puede reenviar el correo:\n\n" +
                            string.Join(Environment.NewLine, errors.Select(error => $"• {error}")),
                "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (MessageBox.Show($"Se creará y enviará un correo nuevo para '{originalRecord.BrokerName}'.\n\nEl envío anterior no será modificado. ¿Desea continuar?",
                "Confirmar reenvío", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        SetBusy(true);
        StatusTextBlock.Text = "Enviando el correo nuevo...";
        try
        {
            var results = await _outlookService.SendBatchAsync([request]);
            var result = results.Single();
            var archivedPaths = new List<string>();
            var recordError = result.ErrorMessage;
            if (result.WasSuccessful)
            {
                try
                {
                    archivedPaths = _archiveService.Archive(request.BrokerName, request.AttachmentPaths);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    recordError = ex.Message;
                }
            }

            NewRecord = new SentEmailRecord
            {
                BrokerId = request.BrokerId,
                BrokerName = request.BrokerName,
                BrokerPrimaryRecipients = [.. request.BrokerPrimaryRecipients],
                AssistantRecipients = [.. request.AssistantRecipients],
                ToRecipients = [.. request.ToRecipients],
                CcRecipients = [.. request.CcRecipients],
                Subject = request.Subject,
                Body = request.Body,
                SentAt = DateTimeOffset.Now,
                ArchivedAttachmentPaths = archivedPaths,
                WasSuccessful = result.WasSuccessful,
                ErrorMessage = recordError,
                ResendOfRecordId = originalRecord.Id,
                PaymentGenerationId = originalRecord.PaymentGenerationId
            };
            _records.Insert(0, NewRecord);
            await _runtimeData.SaveRecentSendsAsync(_records);

            if (result.WasSuccessful)
            {
                StatusTextBlock.Text = string.IsNullOrWhiteSpace(recordError)
                    ? "El correo nuevo fue enviado y registrado."
                    : recordError;
                MessageBox.Show(StatusTextBlock.Text,
                    "Reenvío completado", MessageBoxButton.OK,
                    string.IsNullOrWhiteSpace(recordError) ? MessageBoxImage.Information : MessageBoxImage.Warning);
                DialogResult = true;
            }
            else
            {
                StatusTextBlock.Text = result.ErrorMessage;
                MessageBox.Show(result.ErrorMessage,
                    "No se pudo reenviar", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("Error inesperado al reenviar un correo.", ex);
            StatusTextBlock.Text = ex.Message;
            MessageBox.Show($"No fue posible completar el reenvío.\n\n{ex.Message}",
                "Error de reenvío", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        ClearHistoryButton.IsEnabled = !busy;
        CommandPanel.IsEnabled = !busy;
        RecordsComboBox.IsEnabled = !busy;
        AttachmentsList.IsEnabled = !busy;
        SubjectTextBox.IsEnabled = !busy;
        CcTextBox.IsEnabled = !busy;
    }

    private RecipientResolutionResult ResolveRecipients(SentEmailRecord record, Broker? broker, string ccText)
    {
        if (broker is not null)
        {
            return _validationService.ResolveRecipients(broker, ccText);
        }

        _validationService.TryParseAddresses(
            string.Join(";", record.ToRecipients),
            true,
            out var to,
            out var toErrors);
        _validationService.TryParseAddresses(ccText, false, out var cc, out var ccErrors);
        var toSet = new HashSet<string>(to, StringComparer.OrdinalIgnoreCase);
        return new RecipientResolutionResult
        {
            BrokerPrimaryRecipients = record.BrokerPrimaryRecipients.Count > 0
                ? [.. record.BrokerPrimaryRecipients]
                : [.. to],
            AssistantRecipients = [.. record.AssistantRecipients],
            ToRecipients = to,
            CcRecipients = cc.Where(address => !toSet.Contains(address)).ToList(),
            Errors = [.. toErrors, .. ccErrors.Select(error => $"CC: {error}")]
        };
    }
}
