using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Services.Expirations;
using Microsoft.Win32;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsWindow : Window
{
    private readonly IAppUserRepository _appUsers;
    private readonly IExpirationsAnalysisCoordinator _coordinator;
    private readonly IExpirationsBrokerConfigurationService _configurationService;
    private readonly IExpirationsRoutingAdministrationService? _routingAdministrationService;
    private readonly IExpirationsGenerationService _generationService;
    private readonly IExpirationsEmailSettingsService _emailSettingsService;
    private readonly ExpirationsSendPreparationService _sendPreparationService;
    private readonly IExpirationsOutlookSender _outlookSender;
    private readonly IExpirationsSendHistoryRepository _sendHistory;
    private readonly IExpirationsLocalSettingsService _localSettingsService;
    private readonly SignatureImageService _signatureService;
    private readonly GeneratedFileViewerService _generatedFileViewerService;
    private readonly AssociatedWorkbookEditService _associatedWorkbookEditService;
    private readonly ExpirationsBatchFileAssociationService _batchFileAssociationService;
    private readonly ExpirationsWindowState _state;
    private readonly FileLogger _logger;
    private int _readinessVersion;
    private CancellationTokenSource? _readinessCancellation;

    public ExpirationsWindow(
        AppUser currentUser,
        IAppUserRepository appUsers,
        IExpirationsAnalysisCoordinator coordinator,
        IExpirationsBrokerConfigurationService configurationService,
        IExpirationsGenerationService generationService,
        IExpirationsEmailSettingsService emailSettingsService,
        ExpirationsSendPreparationService sendPreparationService,
        IExpirationsOutlookSender outlookSender,
        IExpirationsSendHistoryRepository sendHistory,
        AppDataPaths paths,
        FileLogger logger,
        IExpirationsLocalSettingsService? localSettingsService = null,
        IExpirationsRoutingAdministrationService? routingAdministrationService = null)
    {
        ArgumentNullException.ThrowIfNull(appUsers);
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _routingAdministrationService = routingAdministrationService;
        _generationService = generationService ?? throw new ArgumentNullException(nameof(generationService));
        _emailSettingsService = emailSettingsService ?? throw new ArgumentNullException(nameof(emailSettingsService));
        _sendPreparationService = sendPreparationService ?? throw new ArgumentNullException(nameof(sendPreparationService));
        _outlookSender = outlookSender ?? throw new ArgumentNullException(nameof(outlookSender));
        _sendHistory = sendHistory ?? throw new ArgumentNullException(nameof(sendHistory));
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        _localSettingsService = localSettingsService ?? new ExpirationsLocalSettingsService(paths, logger);
        _signatureService = new SignatureImageService(paths, paths.ExpirationsSignatureDirectory);
        _generatedFileViewerService = new GeneratedFileViewerService(
            new GeneratedFileProcessLauncher(), paths, logger);
        _associatedWorkbookEditService = new AssociatedWorkbookEditService(paths, logger);
        _batchFileAssociationService = new ExpirationsBatchFileAssociationService();
        _logger = logger;
        _state = new ExpirationsWindowState(currentUser);
        _appUsers = appUsers;
        InitializeComponent();
        DataContext = _state;
        if (_coordinator.Snapshot.Process is null)
            _coordinator.SelectProcess(ExpirationsProcess.PreviousMonth);
        _state.ApplySnapshot(_coordinator.Snapshot);
        LoadLocalSignature();
    }

    public event EventHandler? LogoutRequested
    {
        add => _state.LogoutRequested += value;
        remove => _state.LogoutRequested -= value;
    }

    public event EventHandler? ModuleSwitchRequested
    {
        add => _state.ModuleSwitchRequested += value;
        remove => _state.ModuleSwitchRequested -= value;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_state.IsInitialLoadComplete)
            return;

        await RunBusyAsync("Cargando datos de Vencimientos...", async () =>
        {
            await LoadEmailSettingsAsync();
            _state.ApplySnapshot(await _coordinator.RefreshCatalogAndReanalyzeAsync());
            _state.ApplyOutlook(await _outlookSender.CheckAvailabilityAsync());
            _state.MarkInitialLoadComplete();
        });
    }

    private async void ConnectOutlook_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Conectando con Outlook...", async () =>
        {
            var connection = await _outlookSender.CheckAvailabilityAsync();
            _state.ApplyOutlook(connection);
            MessageBox.Show(
                connection.Message,
                "Conexión con Outlook",
                MessageBoxButton.OK,
                connection.Available ? MessageBoxImage.Information : MessageBoxImage.Warning);
        });
    }

    private void OutlookAccount_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_state.IsRefreshingOutlookAccounts || _state.SelectedOutlookAccountEmail is not { Length: > 0 } account)
            return;
        try
        {
            _state.ApplyOutlookSelection(_outlookSender.SelectSendingAccount(account));
        }
        catch (Exception ex)
        {
            _logger.Error("No fue posible seleccionar la cuenta de Outlook para Vencimientos.", ex);
            _state.ApplyOutlookSelectionFailure("No fue posible seleccionar la cuenta de envío.");
            MessageBox.Show(ex.Message, "Cuenta de Outlook", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ProcessComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_state.IsProcessSelectionChanging)
            return;
        var previous = e.RemovedItems.OfType<ExpirationsProcessOption>().FirstOrDefault();
        if (_state.HasEditableChanges && previous is not null)
        {
            var choice = MessageBox.Show(
                "Hay cambios pendientes en la plantilla o firma. ¿Desea guardarlos antes de cambiar de proceso?",
                "Cambiar proceso",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            if (choice == MessageBoxResult.Cancel)
            {
                _state.RestoreProcessSelection(previous);
                return;
            }
            if (choice == MessageBoxResult.Yes && !await SaveEditableConfigurationAsync(showSuccessMessage: false))
            {
                _state.RestoreProcessSelection(previous);
                return;
            }
        }
        _coordinator.SelectProcess(_state.SelectedProcessOption?.Value);
        _state.ApplySnapshot(_coordinator.Snapshot);
        await LoadEmailSettingsAsync();
        await EvaluateNextMonthReadinessAsync();
    }

    private void SelectFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar reporte general de Vencimientos",
            Filter = "Archivos de Excel (*.xlsx)|*.xlsx",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        _coordinator.SelectFile(dialog.FileName);
        _state.ResetPremiumColumnOptions();
        _state.ApplySnapshot(_coordinator.Snapshot);
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Analizando archivo...", async () =>
        {
            var snapshot = await _coordinator.AnalyzeAsync();
            if (snapshot.RequiresWorkbookSelection)
            {
                var inspection = await _coordinator.InspectWorkbookAsync();
                var selection = new ExpirationsWorkbookSelectionWindow(inspection) { Owner = this };
                if (selection.ShowDialog() == true && selection.Options is not null)
                    snapshot = await _coordinator.AnalyzeAsync(selection.Options);
            }

            _state.ApplySnapshot(snapshot);
            await EvaluateNextMonthReadinessAsync();
            if (snapshot.ReadResult is { IsSuccess: false } && !snapshot.RequiresWorkbookSelection)
            {
                MessageBox.Show(
                    string.Join(Environment.NewLine, snapshot.Messages),
                    "Análisis de Vencimientos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        });
    }

    private async void ResolveSelected_Click(object sender, RoutedEventArgs e)
    {
        var issue = _state.SelectedPendingIssue;
        if (issue is null)
            return;
        if (issue.Status == ExpirationsBrokerResolutionStatus.MissingBroker)
        {
            MessageBox.Show(
                "La fila no contiene un corredor. Corrija el archivo fuente y vuelva a analizarlo.",
                "Sin corredor",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        if (issue.Status == ExpirationsBrokerResolutionStatus.InactiveBroker)
        {
            MessageBox.Show(
                "El corredor está identificado, pero se encuentra inactivo para Vencimientos.",
                "Corredor inactivo",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new ExpirationsBrokerResolutionWindow(issue, _coordinator.Snapshot.Catalog)
        {
            Owner = this
        };
        if (dialog.ShowDialog() != true || dialog.SelectedBrokerId is not { } brokerId)
            return;

        if (issue.Status == ExpirationsBrokerResolutionStatus.Ambiguous)
        {
            var result = _coordinator.ApplyManualOverride(issue.RowNumber, issue.ComponentIndex, brokerId);
            _state.ApplySnapshot(result.Snapshot);
            await EvaluateNextMonthReadinessAsync();
            MessageBox.Show(
                result.Message,
                "Resolución manual",
                MessageBoxButton.OK,
                result.Applied ? MessageBoxImage.Information : MessageBoxImage.Warning);
            return;
        }

        await RunBusyAsync("Actualizando asociaciones...", async () =>
        {
            var result = await _coordinator.ConfirmAssociationAsync(new ExpirationsAssociationConfirmation(
                issue.RowNumber,
                issue.ComponentIndex,
                brokerId,
                dialog.SelectedKind));
            _state.ApplySnapshot(result.Snapshot);
            await EvaluateNextMonthReadinessAsync();
            var warning = result.Outcome is
                ExpirationsAssociationConfirmationOutcome.Failed or
                ExpirationsAssociationConfirmationOutcome.Rejected or
                ExpirationsAssociationConfirmationOutcome.ConcurrencyConflict or
                ExpirationsAssociationConfirmationOutcome.SessionOverride;
            MessageBox.Show(
                result.Message,
                "Asociación de Vencimientos",
                MessageBoxButton.OK,
                warning ? MessageBoxImage.Warning : MessageBoxImage.Information);
        });
    }

    private async void ExcludeSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_state.SelectedPendingIssue is not { } issue)
            return;
        if (MessageBox.Show(
                "Este valor se marcará como no distribuible para Vencimientos.\n" +
                "Las filas que contengan esta identificación no generarán archivos ni correos.\n" +
                "La exclusión se aplicará también en futuros análisis.\n\n¿Desea continuar?",
                "No corresponde distribución",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Guardando exclusión...", async () =>
        {
            var result = await _coordinator.ExcludeAsync(issue.RowNumber, issue.ComponentIndex);
            _state.ApplySnapshot(result.Snapshot);
            MessageBox.Show(
                result.Message,
                "No corresponde distribución",
                MessageBoxButton.OK,
                result.Persisted ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await EvaluateNextMonthReadinessAsync();
        });
    }

    private async void ConfigureBrokers_Click(object sender, RoutedEventArgs e)
    {
        if (_state.SelectedProcessOption?.Value is not { } process)
            return;
        var management = new ExpirationsBrokerManagementWindow(
            _configurationService,
            _routingAdministrationService,
            process) { Owner = this };
        _ = management.ShowDialog();
        if (!management.HasSavedChanges)
            return;

        await RunBusyAsync("Actualizando corredores...", async () =>
        {
            var snapshot = await _coordinator.RefreshCatalogAndReanalyzeAsync();
            _state.ApplySnapshot(snapshot);
            await EvaluateNextMonthReadinessAsync();
        });
    }

    private async Task LoadEmailSettingsAsync()
    {
        if (_state.SelectedProcessOption?.Value is not { } process)
            return;
        var loaded = await _emailSettingsService.LoadAsync(process);
        if (_state.SelectedProcessOption?.Value != process)
            return;
        _state.LoadEmailSettings(loaded);
    }

    private async Task<bool> SaveEditableConfigurationAsync(bool showSuccessMessage)
    {
        if (!_state.HasEditableChanges)
        {
            if (showSuccessMessage)
                MessageBox.Show("Sin cambios pendientes.", "Guardar", MessageBoxButton.OK, MessageBoxImage.Information);
            return true;
        }
        if (_state.EmailSettingsSnapshot is not { } snapshot)
            return false;

        var result = _state.HasEmailSettingsChanges
            ? await _emailSettingsService.SaveAsync(
                snapshot,
                _state.Subject,
                _state.Message,
                _state.CommonCcText)
            : new ExpirationsEmailSettingsSaveResult
            {
                Outcome = ExpirationsEmailSettingsSaveOutcome.Updated,
                Snapshot = snapshot,
                Message = "La configuración local fue guardada."
            };
        if (result.Outcome == ExpirationsEmailSettingsSaveOutcome.ValidationFailed)
        {
            MessageBox.Show(result.Message, "Revise la configuración", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        if (result.Outcome == ExpirationsEmailSettingsSaveOutcome.ConcurrencyConflict)
        {
            _state.LoadEmailSettings(result.Snapshot);
            MessageBox.Show(result.Message, "Configuración actualizada", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var previousSignaturePath = _state.PersistedSignatureImagePath;
        _localSettingsService.Save(new ExpirationsLocalSettings
        {
            SignatureImagePath = _state.SignatureImagePath
        });
        _state.MarkEditableConfigurationSaved(result.Snapshot);
        if (!PathsEqual(previousSignaturePath, _state.SignatureImagePath))
            _signatureService.DeleteIfManaged(previousSignaturePath);
        _state.InvalidateSendPreview();
        await EvaluateSendPreflightAsync();
        if (showSuccessMessage)
            MessageBox.Show("La configuración de Vencimientos fue guardada.", "Guardar", MessageBoxButton.OK, MessageBoxImage.Information);
        return true;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync("Guardando configuración...", async () =>
            _ = await SaveEditableConfigurationAsync(showSuccessMessage: true));
    }

    private async void NewSend_Click(object sender, RoutedEventArgs e)
    {
        if (_state.HasActivePreparation && MessageBox.Show(
                "¿Desea iniciar un nuevo envío?\nSe limpiará la preparación actual, pero no se eliminarán configuraciones, archivos generados ni historial.",
                "Nuevo envío",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Iniciando una nueva preparación...", async () =>
        {
            var process = _state.SelectedProcessOption?.Value;
            _coordinator.ResetPreparation();
            _coordinator.SelectProcess(process);
            _state.ResetTransientState(_coordinator.Snapshot);
            _state.ApplySnapshot(await _coordinator.RefreshCatalogAndReanalyzeAsync());
            await Task.CompletedTask;
        });
    }

    private async void ReloadData_Click(object sender, RoutedEventArgs e)
    {
        if ((_state.HasEditableChanges || _state.HasActivePreparation) && MessageBox.Show(
                "Se recargarán corredores, perfiles, asistentes, asociaciones y la plantilla del proceso. El archivo y análisis se conservarán, pero un lote generado actual deberá reconstruirse. ¿Desea continuar?",
                "Recargar datos",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Recargando datos...", async () =>
        {
            _state.ApplySnapshot(await _coordinator.RefreshCatalogAndReanalyzeAsync());
            await LoadEmailSettingsAsync();
            await EvaluateNextMonthReadinessAsync();
        });
    }

    private void SelectSignature_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar firma de Vencimientos",
            Filter = "Imágenes (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var managedPath = _signatureService.Import(dialog.FileName);
            _state.SetSignature(managedPath, _signatureService.LoadPreview(managedPath), "Firma lista para guardar.");
        }
        catch (SignatureImageException ex)
        {
            MessageBox.Show(ex.Message, "Firma de Vencimientos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveSignature_Click(object sender, RoutedEventArgs e) =>
        _state.SetSignature(null, null, "La firma se quitará al guardar.");

    private void OpenSourceFile_Click(object sender, RoutedEventArgs e) => OpenPath(_state.SourcePath);

    private void OpenSourceFolder_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_state.SourcePath);
        if (!string.IsNullOrWhiteSpace(directory))
            OpenPath(directory);
    }

    private void ViewGeneratedFiles_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ExpirationsBrokerRow { CanManageFiles: true } row)
            return;
        IReadOnlyList<ExpirationsGeneratedFile> LoadFiles() =>
            (_state.GenerationBatch?.Files ?? [])
            .Where(file => file.BrokerId == row.BrokerId)
            .ToList();
        void PersistReplacement(ExpirationsGeneratedFile file, string sha256)
        {
            if (_state.GenerationBatch is { } batch)
            {
                _state.ReplaceGenerationBatch(
                    _batchFileAssociationService.UpdateAuthorizedReplacement(batch, file, sha256));
            }
        }
        void Unlink(ExpirationsGeneratedFile file)
        {
            if (_state.GenerationBatch is { } batch)
                _state.ReplaceGenerationBatch(_batchFileAssociationService.Remove(batch, file));
        }
        ExpirationsManualFileAddResult AddManual(string sourcePath)
        {
            if (_state.GenerationBatch is not { } batch)
                return new ExpirationsManualFileAddResult(new ExpirationsGenerationBatch(), null,
                    "Primero genere un batch de Vencimientos.");
            var result = _batchFileAssociationService.AddManual(batch, row.BrokerId, row.BrokerName, sourcePath);
            if (result.Succeeded)
                _state.ReplaceGenerationBatch(result.Batch);
            return result;
        }

        new ExpirationsGeneratedFilesWindow(
            row.BrokerName,
            LoadFiles,
            _generatedFileViewerService,
            _associatedWorkbookEditService,
            PersistReplacement,
            Unlink,
            AddManual,
            async () => { _ = await EvaluateSendPreflightAsync(); },
            row.IsParticipant)
        {
            Owner = this
        }.ShowDialog();
    }

    private static void OpenPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void LoadLocalSignature()
    {
        var path = _localSettingsService.Load().SignatureImagePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            _state.LoadPersistedSignature(null, null, "No hay una firma configurada.");
            return;
        }
        try
        {
            _state.LoadPersistedSignature(path, _signatureService.LoadPreview(path), "Firma local configurada.");
        }
        catch (SignatureImageException ex)
        {
            _state.LoadPersistedSignature(null, null, $"Firma no disponible: {ex.Message}");
        }
    }

    private async void NextMonthPeriod_Changed(object sender, RoutedEventArgs e)
    {
        _state.InvalidateGenerationReadiness(
            "Seleccione un mes y año válidos; después se validarán la configuración especial y las primas.");
        await EvaluateNextMonthReadinessAsync();
    }

    private async void SelectPremiumColumns_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var workbook = _coordinator.Snapshot.ReadResult?.Workbook;
            if (workbook is null)
                return;
            var inspection = await _coordinator.InspectWorkbookAsync();
            var selection = new ExpirationsPremiumColumnSelectionWindow(
                inspection,
                workbook.WorksheetName,
                workbook.HeaderRowNumber,
                _state.PremiumColumnOptions) { Owner = this };
            if (selection.ShowDialog() != true || selection.Options is null)
                return;
            _state.SetPremiumColumnOptions(selection.Options);
            await EvaluateNextMonthReadinessAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible seleccionar las columnas.\n\n{ex.Message}",
                "Prima y Moneda",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async Task EvaluateNextMonthReadinessAsync()
    {
        var version = ++_readinessVersion;
        _readinessCancellation?.Cancel();
        if (_state.SelectedProcessOption?.Value != ExpirationsProcess.NextMonth ||
            _coordinator.Snapshot.Analysis?.CanGenerate != true)
            return;
        if (!_state.TryGetNextMonthPeriod(out var period))
        {
            _state.InvalidateGenerationReadiness("Seleccione un mes y año válidos para el reporte.");
            return;
        }

        var readinessCancellation = new CancellationTokenSource();
        _readinessCancellation = readinessCancellation;
        _state.InvalidateGenerationReadiness("Validando configuración, columnas y primas...");
        try
        {
            var preparation = await _coordinator.PrepareGenerationAsync(
                period!,
                _state.PremiumColumnOptions,
                readinessCancellation.Token);
            if (version != _readinessVersion)
                return;
            _state.ApplySnapshot(preparation.Snapshot);
            _state.SetGenerationReadiness(
                preparation.CanGenerate,
                preparation.CanGenerate
                    ? "Configuración especial, período y primas validados. Listo para generar."
                    : preparation.ErrorMessage,
                preparation.RequiresManualPremiumColumnSelection);
        }
        catch (OperationCanceledException) when (readinessCancellation.IsCancellationRequested)
        {
            // Una selección más reciente reemplazó esta validación.
        }
        catch (Exception ex)
        {
            if (version == _readinessVersion)
                _state.InvalidateGenerationReadiness($"No fue posible validar la generación: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_readinessCancellation, readinessCancellation))
                _readinessCancellation = null;
            readinessCancellation.Dispose();
        }
    }

    private async void GenerateFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Seleccione la carpeta donde se guardará esta generación",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        await RunBusyAsync("Preparando generación...", async () =>
        {
            ExpirationsPeriod? period = null;
            ExpirationsGenerationPreparationResult preparation;
            if (_state.SelectedProcessOption?.Value == ExpirationsProcess.NextMonth)
            {
                if (!_state.TryGetNextMonthPeriod(out period))
                {
                    MessageBox.Show(
                        "Seleccione un mes y año válidos para generar el mes siguiente.",
                        "Generación de Vencimientos",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
                preparation = await _coordinator.PrepareGenerationAsync(
                    period!,
                    _state.PremiumColumnOptions);
            }
            else
            {
                preparation = await _coordinator.PrepareGenerationAsync();
            }
            _state.ApplySnapshot(preparation.Snapshot);
            if (!preparation.CanGenerate)
            {
                MessageBox.Show(
                    preparation.ErrorMessage,
                    "Generación de Vencimientos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var progress = new Progress<ExpirationsGenerationProgress>(value =>
                _state.SetOperationStatus($"Generando archivo {value.CurrentFile} de {value.TotalFiles}..."));
            var batch = await _generationService.GenerateAsync(
                new ExpirationsGenerationRequest(
                    preparation.Context!,
                    dialog.FolderName,
                    period,
                    _state.PremiumColumnOptions),
                progress);
            _state.ApplyGenerationBatch(batch);
            await EvaluateSendPreflightAsync();
            MessageBox.Show(
                $"Generación completada.\n\nCorredores con archivos: {batch.Files.Select(file => file.BrokerId).Distinct().Count()}\nArchivos generados: {batch.Files.Count}\nCarpeta: {batch.OutputDirectory}",
                "Generación de Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }

    private async void SendSelected_Click(object sender, RoutedEventArgs e) =>
        await SendBrokersAsync(_state.SelectedBrokerIds.ToList());

    private async void SendAll_Click(object sender, RoutedEventArgs e) =>
        await SendBrokersAsync(_state.EligibleBrokerIds.ToList());

    private async Task SendBrokersAsync(IReadOnlyCollection<Guid> brokerIds)
    {
        await RunBusyAsync("Validando el lote antes de abrir Outlook...", async () =>
        {
            if (_state.GenerationBatch is not { } batch)
                return;
            var preparation = await _sendPreparationService.PrepareSelectedAsync(
                batch,
                brokerIds,
                _state.SignatureImagePath);
            if (!preparation.CanSend)
            {
                _ = await EvaluateSendPreflightAsync();
                MessageBox.Show(
                    string.Join(Environment.NewLine, preparation.Errors),
                    "Preflight de Vencimientos",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var review = new ExpirationsSendReviewWindow(preparation, _outlookSender, _sendHistory) { Owner = this };
            _ = review.ShowDialog();
            if (review.CompletedResult is { } result)
                _state.ApplySendResult(result);
        });
    }

    private void OpenSendHistory_Click(object sender, RoutedEventArgs e)
    {
        var history = new ExpirationsSendHistoryWindow(
            _sendHistory,
            new ExpirationsRetryPreparationService(signatureImagePath: _state.SignatureImagePath),
            _state.GenerationBatch,
            _outlookSender)
        {
            Owner = this
        };
        _ = history.ShowDialog();
    }

    private async Task<ExpirationsSendPreparationResult?> EvaluateSendPreflightAsync()
    {
        if (_state.GenerationBatch is not { } batch)
            return null;
        ExpirationsSendPreparationResult preparation;
        try
        {
            preparation = await _sendPreparationService.PrepareAsync(batch, _state.SignatureImagePath);
        }
        catch (Exception ex)
        {
            preparation = new ExpirationsSendPreparationResult
            {
                Process = batch.Process,
                Errors = [$"No fue posible preparar el envío: {ex.Message}"]
            };
        }
        _state.ApplySendPreparation(preparation);
        return preparation;
    }

    private void OpenGeneratedFolder_Click(object sender, RoutedEventArgs e)
    {
        OpenPath(_state.GeneratedOutputDirectory);
    }

    private async Task RunBusyAsync(string status, Func<Task> operation)
    {
        _state.SetBusy(true, status);
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"No fue posible completar la operación.\n\n{ex.Message}",
                "Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            _state.SetBusy(false);
        }
    }

    private void ManageAccess_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            AppUserAuthorization.DemandAdmin(_state.CurrentUser);
            new UserAdministrationWindow(_appUsers, _state.CurrentUser) { Owner = this }.ShowDialog();
        }
        catch (AppUserAuthorizationException ex)
        {
            MessageBox.Show(ex.Message, "Administración de accesos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SwitchModule_Click(object sender, RoutedEventArgs e)
    {
        if (!_state.CanSwitchModule)
        {
            MessageBox.Show(
                _state.IsBusy
                    ? "Espere a que finalice la operación actual antes de cambiar de módulo."
                    : "El usuario actual no tiene acceso a ambos módulos.",
                "Cambiar módulo",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (_state.HasActivePreparation && MessageBox.Show(
                "La preparación actual de Vencimientos existe solo en memoria. Al cambiar a Comisiones se conservarán configuraciones, archivos generados e historial, pero deberá iniciar nuevamente esta preparación al regresar. ¿Desea continuar?",
                "Cambiar módulo",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Guardando antes de cambiar de módulo...", async () =>
        {
            if (!await SaveEditableConfigurationAsync(showSuccessMessage: false))
                return;
            if (_state.RequestModuleSwitch(allowWhileNavigationBusy: true))
                Close();
        });
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (_state.IsBusy)
        {
            MessageBox.Show("Espere a que finalice la operación actual antes de cerrar sesión.",
                "Cerrar sesión", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(
                "¿Desea cerrar la sesión de Firebase en este equipo?",
                "Cerrar sesión",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        await RunBusyAsync("Guardando antes de cerrar sesión...", async () =>
        {
            if (!await SaveEditableConfigurationAsync(showSuccessMessage: false))
                return;
            _state.RequestLogout();
            Close();
        });
    }
}

internal sealed class ExpirationsWindowState : INotifyPropertyChanged
{
    private readonly ObservableCollection<ExpirationsBrokerRow> _brokerRows = [];
    private readonly ICollectionView _brokerRowsView;
    private readonly HashSet<Guid> _eligibleBrokerIds = [];
    private readonly HashSet<Guid> _selectedBrokerIds = [];
    private readonly Dictionary<Guid, ExpirationsSendResultItem> _sendStatuses = [];
    private ExpirationsProcessOption? _selectedProcessOption;
    private ExpirationsAnalysisSessionSnapshot _snapshot = new();
    private ExpirationsPendingIssue? _selectedPendingIssue;
    private bool _isBusy;
    private bool _isProcessSelectionChanging;
    private bool _isInitialLoadComplete;
    private string _operationStatusText = "Listo.";
    private ExpirationsGenerationBatch? _generationBatch;
    private ExpirationsSendPreparationResult? _sendPreparation;
    private ExpirationsSendExecutionResult? _sendResult;
    private ExpirationsMonthOption? _selectedMonthOption;
    private string _nextMonthYearText = string.Empty;
    private ExpirationsPremiumColumnOptions? _premiumColumnOptions;
    private bool _nextMonthGenerationReady;
    private bool _premiumColumnSelectionRequired;
    private string _generationReadinessText = "Seleccione un mes y año para validar la generación.";
    private ExpirationsEmailSettingsSnapshot? _emailSettingsSnapshot;
    private string _subject = string.Empty;
    private string _message = string.Empty;
    private string _commonCcText = string.Empty;
    private string _persistedSubject = string.Empty;
    private string _persistedMessage = string.Empty;
    private string _persistedCommonCcText = string.Empty;
    private string? _signatureImagePath;
    private string? _persistedSignatureImagePath;
    private BitmapSource? _signaturePreview;
    private string _signatureStateText = "No hay una firma configurada.";
    private string _searchText = string.Empty;
    private string _outlookStatusText = "Comprobando Outlook...";
    private Brush _outlookStatusBrush = Brushes.Goldenrod;
    private string? _selectedOutlookAccountEmail;
    private bool _isRefreshingOutlookAccounts;
    private bool _resetSelectionOnNextPreparation;
    private bool _isBulkSelectionUpdate;

    public ExpirationsWindowState(AppUser currentUser)
    {
        AppUserAuthorization.DemandExpirationsAccess(currentUser);
        CurrentUser = currentUser;
        ProcessOptions =
        [
            new ExpirationsProcessOption(ExpirationsProcess.PreviousMonth, "Pendientes del mes anterior"),
            new ExpirationsProcessOption(ExpirationsProcess.NextMonth, "Vencimientos del mes siguiente")
        ];
        MonthOptions = Enumerable.Range(1, 12)
            .Select(month => new ExpirationsMonthOption(
                month,
                CultureInfo.GetCultureInfo("es-CR").TextInfo.ToTitleCase(
                    CultureInfo.GetCultureInfo("es-CR").DateTimeFormat.GetMonthName(month))))
            .ToArray();
        _brokerRowsView = CollectionViewSource.GetDefaultView(_brokerRows);
        _brokerRowsView.Filter = FilterBrokerRow;
    }

    public AppUser CurrentUser { get; }
    public IReadOnlyList<ExpirationsProcessOption> ProcessOptions { get; }
    public IReadOnlyList<ExpirationsMonthOption> MonthOptions { get; }
    public ICollectionView BrokerRowsView => _brokerRowsView;
    public ObservableCollection<string> OutlookAccounts { get; } = [];
    public ExpirationsEmailSettingsSnapshot? EmailSettingsSnapshot => _emailSettingsSnapshot;
    public string SignedInUserText => $"{CurrentUser.DisplayName} · {CurrentUser.Email}";
    public string ApplicationVersionText
    {
        get
        {
            var informationalVersion = typeof(ExpirationsWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;
            var version = string.IsNullOrWhiteSpace(informationalVersion)
                ? typeof(ExpirationsWindow).Assembly.GetName().Version?.ToString(3) ?? "desconocida"
                : informationalVersion.Split('+', 2)[0];
            return $"Versión {version}";
        }
    }
    public Visibility AdminAccessVisibility => CurrentUser.IsActive && CurrentUser.Role == AppUserRole.Admin
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility SwitchModuleVisibility => ModuleAccessResolver.CanSwitchModules(CurrentUser)
            ? Visibility.Visible
            : Visibility.Collapsed;
    public bool IsInitialLoadComplete => _isInitialLoadComplete;
    public bool IsProcessSelectionChanging => _isProcessSelectionChanging;
    public string SourcePath => _snapshot.SourcePath;
    public string SourceFileName => SourcePath.Length == 0 ? "Ningún archivo seleccionado" : Path.GetFileName(SourcePath);
    public bool IsBusy => _isBusy;
    public bool CanSelectFile => !IsBusy;
    public bool CanConfigureBrokers => !IsBusy;
    public bool CanConfigureEmail => !IsBusy && SelectedProcessOption is not null;
    public bool CanOpenSendHistory => !IsBusy;
    public bool CanOpenSourceFile => !IsBusy && File.Exists(SourcePath);
    public bool CanOpenSourceFolder => !IsBusy && Directory.Exists(Path.GetDirectoryName(SourcePath));
    public bool CanOpenGeneratedFolder => !IsBusy && Directory.Exists(GeneratedOutputDirectory);
    public bool CanAnalyze => !IsBusy && SelectedProcessOption is not null && SourcePath.Length > 0;
    public bool CanResolve => !IsBusy && SelectedPendingIssue?.CanResolve == true;
    public bool CanExclude => !IsBusy && SelectedPendingIssue?.CanResolve == true;
    public bool CanSelectPremiumColumns => !IsBusy &&
        _snapshot.Process == ExpirationsProcess.NextMonth && _snapshot.ReadResult?.Workbook is not null;
    public bool CanGenerate => !IsBusy &&
        _snapshot.Analysis is { CanGenerate: true, TotalRows: > 0 } &&
        _snapshot.Distribution.Count > 0 &&
        (_snapshot.Process == ExpirationsProcess.PreviousMonth ||
         (_snapshot.Process == ExpirationsProcess.NextMonth && _nextMonthGenerationReady));
    public bool CanReviewAndSend => CanSendAll;
    public bool CanSendSelected => !IsBusy && _generationBatch is not null &&
        _sendPreparation is not null && _selectedBrokerIds.Count > 0;
    public bool CanSendAll => !IsBusy && _generationBatch is not null &&
        _sendPreparation is not null && _eligibleBrokerIds.Count > 0;
    public bool CanSwitchModule => !IsBusy && SwitchModuleVisibility == Visibility.Visible;
    public bool CanSave => !IsBusy && _emailSettingsSnapshot is not null && HasEditableChanges;
    public bool HasActivePreparation => SourcePath.Length > 0 || _snapshot.Analysis is not null ||
        _generationBatch is not null || _sendResult is not null;
    public bool HasEditableChanges =>
        HasEmailSettingsChanges ||
        !PathValuesEqual(_signatureImagePath, _persistedSignatureImagePath);
    public bool HasEmailSettingsChanges =>
        !string.Equals(_subject, _persistedSubject, StringComparison.Ordinal) ||
        !string.Equals(_message, _persistedMessage, StringComparison.Ordinal) ||
        !string.Equals(_commonCcText, _persistedCommonCcText, StringComparison.Ordinal);
    public string EmailSettingsStateText => HasEmailSettingsChanges
        ? "Cambios sin guardar. Guarde la plantilla de correo antes de enviar."
        : _emailSettingsSnapshot?.Exists == true
            ? "Plantilla guardada para este proceso."
            : "Aún no existe una plantilla guardada para este proceso.";
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool IsRefreshingOutlookAccounts => _isRefreshingOutlookAccounts;
    public string OutlookStatusText => _outlookStatusText;
    public Brush OutlookStatusBrush => _outlookStatusBrush;
    public IReadOnlyCollection<Guid> EligibleBrokerIds => _eligibleBrokerIds;
    public IReadOnlyCollection<Guid> SelectedBrokerIds => _selectedBrokerIds;
    public bool? AreAllEligibleSelected
    {
        get
        {
            if (_eligibleBrokerIds.Count == 0)
                return false;
            var selectedEligibleCount = _eligibleBrokerIds.Count(_selectedBrokerIds.Contains);
            return selectedEligibleCount == 0
                ? false
                : selectedEligibleCount == _eligibleBrokerIds.Count
                    ? true
                    : null;
        }
        set => SelectAllEligible(value == true);
    }
    public string OperationStatusText => _operationStatusText;
    public Visibility ResultVisibility => _snapshot.Analysis is not null || _snapshot.ReadResult is not null
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility AnalysisVisibility => _snapshot.Analysis is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility => PendingItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadyVisibility => _snapshot.Analysis is not null && PendingItems.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility GenerationVisibility => AnalysisVisibility;
    public Visibility GenerationResultVisibility => _generationBatch is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SendResultVisibility => _sendResult is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NextMonthPeriodVisibility => SelectedProcessOption?.Value == ExpirationsProcess.NextMonth
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility PremiumColumnSelectionVisibility => _premiumColumnSelectionRequired
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string GenerationReadinessText => _generationReadinessText;
    public int TotalRows => _snapshot.Analysis?.TotalRows ?? 0;
    public int ResolvedRows => _snapshot.Analysis?.ResolvedRows ?? 0;
    public int PendingRows => _snapshot.Analysis?.RowsWithBlockingIssues ?? 0;
    public int ExcludedCount => _snapshot.Analysis?.ExcludedComponents ?? 0;
    public int DestinationCount => PreviewItems.Count;
    public int CatalogCount => _snapshot.Catalog.Count(item => item.IsActive);
    public string BrokerSummaryText =>
        $"Corredores: {CatalogCount}  ·  Participan: {DestinationCount}  ·  Con pendientes: {PendingRows}  ·  Excluidos: {ExcludedCount}  ·  Generados: {GeneratedBrokerCount}";
    public IReadOnlyList<ExpirationsDistributionPreviewItem> PreviewItems => _snapshot.Distribution;
    public IReadOnlyList<ExpirationsPendingIssue> PendingItems => _snapshot.PendingIssues;
    public string ReportStatusText => _sendResult is not null ? "Enviado" :
        _generationBatch is not null ? "Generado" :
        _snapshot.Analysis is { CanGenerate: true } ? "Listo" :
        _snapshot.Analysis is not null && PendingItems.Count > 0 ? "Con pendientes" :
        _snapshot.ReadResult is not null ? "Analizado" : "Sin analizar";
    public string StatusText => _snapshot.Analysis switch
    {
        { CanGenerate: true } => "Análisis completo.",
        { } when PendingItems.Count == 1 => "El análisis tiene 1 elemento pendiente de revisión.",
        { } => $"El análisis tiene {PendingItems.Count} elementos pendientes de revisión.",
        _ when _snapshot.RequiresWorkbookSelection => "No se identificó automáticamente la columna Corredor.",
        _ => "Seleccione y analice el reporte general."
    };
    public string StatusDetailText => _snapshot.Analysis?.CanGenerate == true
        ? _snapshot.Process == ExpirationsProcess.PreviousMonth
            ? "Todas las pólizas tienen un corredor identificado. Listo para la generación."
            : "Todas las pólizas tienen un corredor identificado."
        : _snapshot.Messages.Count > 0
            ? string.Join(" ", _snapshot.Messages)
            : "La tabla muestra el catálogo activo de Vencimientos.";
    public string ReadyText => _snapshot.Process == ExpirationsProcess.PreviousMonth
        ? "El análisis está completo y listo para la generación."
        : "El análisis está completo.";
    public int GeneratedFileCount => _generationBatch?.Files.Count ?? 0;
    public int GeneratedBrokerCount => _generationBatch?.Files.Select(file => file.BrokerId).Distinct().Count() ?? 0;
    public string GeneratedOutputDirectory => _generationBatch?.OutputDirectory ?? string.Empty;
    public string GenerationWarningsText => _generationBatch is { Warnings.Count: > 0 }
        ? string.Join(Environment.NewLine, _generationBatch.Warnings)
        : "Sin advertencias.";
    public string SendReadinessText => _generationBatch switch
    {
        null => "Genere los archivos antes de preparar el envío.",
        _ when _sendPreparation is null => "El preview de envío debe reconstruirse.",
        _ when _sendPreparation.CanSend => $"Preflight completo: {_sendPreparation.Requests.Count} correo(s) listo(s) para revisión.",
        _ => string.Join(Environment.NewLine, _sendPreparation.Errors)
    };
    public string SendSummaryText => _sendResult is null
        ? string.Empty
        : $"Enviados correctamente: {_sendResult.SuccessfulCount} · Fallidos: {_sendResult.FailedCount}" +
          (_sendResult.UnknownCount > 0 ? $" · No confirmados: {_sendResult.UnknownCount}" : string.Empty) +
          (_sendResult.HistoryWarning.Length > 0 ? $"{Environment.NewLine}{_sendResult.HistoryWarning}" : string.Empty);
    public IReadOnlyList<ExpirationsSendResultItem> SendResultItems => _sendResult?.Items ?? [];
    public ExpirationsPremiumColumnOptions? PremiumColumnOptions => _premiumColumnOptions;
    internal ExpirationsGenerationBatch? GenerationBatch => _generationBatch;

    public string Subject
    {
        get => _subject;
        set => SetEditableField(ref _subject, value ?? string.Empty);
    }

    public string Message
    {
        get => _message;
        set => SetEditableField(ref _message, value ?? string.Empty);
    }

    public string CommonCcText
    {
        get => _commonCcText;
        set => SetEditableField(ref _commonCcText, value ?? string.Empty);
    }

    public string? SignatureImagePath => _signatureImagePath;
    public string? PersistedSignatureImagePath => _persistedSignatureImagePath;
    public BitmapSource? SignaturePreview => _signaturePreview;
    public string SignatureFileName => string.IsNullOrWhiteSpace(_signatureImagePath)
        ? "Sin imagen"
        : Path.GetFileName(_signatureImagePath);
    public string SignatureStateText => _signatureStateText;
    public bool HasConfiguredSignature => !string.IsNullOrWhiteSpace(_signatureImagePath);
    public Visibility SignaturePreviewVisibility => _signaturePreview is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SignatureEmptyVisibility => _signaturePreview is null ? Visibility.Visible : Visibility.Collapsed;
    public string SignatureSelectButtonText => HasConfiguredSignature ? "Cambiar imagen" : "Seleccionar imagen";

    public string? SelectedOutlookAccountEmail
    {
        get => _selectedOutlookAccountEmail;
        set
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(_selectedOutlookAccountEmail, normalized, StringComparison.OrdinalIgnoreCase))
                return;
            _selectedOutlookAccountEmail = normalized;
            Notify();
        }
    }

    public void ApplyOutlook(OutlookConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        _isRefreshingOutlookAccounts = true;
        try
        {
            OutlookAccounts.Clear();
            foreach (var account in (connection.AccountEmailAddresses ?? [])
                         .Where(value => !string.IsNullOrWhiteSpace(value))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                OutlookAccounts.Add(account.Trim());
            }
            _selectedOutlookAccountEmail = connection.EmailAddress;
            _outlookStatusText = connection.Message;
            _outlookStatusBrush = !connection.Available
                ? Brushes.IndianRed
                : connection.EmailAddress is null
                    ? Brushes.Goldenrod
                    : Brushes.SeaGreen;
        }
        finally
        {
            _isRefreshingOutlookAccounts = false;
            NotifyAllState();
        }
    }

    public void ApplyOutlookSelection(string account)
    {
        _selectedOutlookAccountEmail = account;
        _outlookStatusText = $"Cuenta de envío seleccionada: {account}.";
        _outlookStatusBrush = Brushes.SeaGreen;
        NotifyAllState();
    }

    public void ApplyOutlookSelectionFailure(string message)
    {
        _selectedOutlookAccountEmail = null;
        _outlookStatusText = message;
        _outlookStatusBrush = Brushes.IndianRed;
        NotifyAllState();
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (string.Equals(_searchText, value, StringComparison.Ordinal)) return;
            _searchText = value ?? string.Empty;
            _brokerRowsView.Refresh();
            Notify();
        }
    }

    public ExpirationsMonthOption? SelectedMonthOption
    {
        get => _selectedMonthOption;
        set
        {
            if (Equals(_selectedMonthOption, value)) return;
            _selectedMonthOption = value;
            _generationBatch = null;
            ClearSendState();
            InvalidateGenerationReadiness("Validación pendiente para el período seleccionado.");
            Notify();
        }
    }

    public string NextMonthYearText
    {
        get => _nextMonthYearText;
        set
        {
            if (string.Equals(_nextMonthYearText, value, StringComparison.Ordinal)) return;
            _nextMonthYearText = value;
            _generationBatch = null;
            ClearSendState();
            InvalidateGenerationReadiness("Validación pendiente para el período seleccionado.");
            Notify();
        }
    }

    public ExpirationsProcessOption? SelectedProcessOption
    {
        get => _selectedProcessOption;
        set
        {
            if (Equals(_selectedProcessOption, value)) return;
            _selectedProcessOption = value;
            _selectedPendingIssue = null;
            _generationBatch = null;
            ClearSendState();
            InvalidateGenerationReadiness("Seleccione un mes y año para validar la configuración especial y las primas.");
            Notify();
            NotifyAllState();
        }
    }

    public ExpirationsPendingIssue? SelectedPendingIssue
    {
        get => _selectedPendingIssue;
        set
        {
            if (ReferenceEquals(_selectedPendingIssue, value)) return;
            _selectedPendingIssue = value;
            Notify();
            Notify(nameof(CanResolve));
            Notify(nameof(CanExclude));
        }
    }

    public event EventHandler? LogoutRequested;
    public event EventHandler? ModuleSwitchRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RequestLogout() => LogoutRequested?.Invoke(this, EventArgs.Empty);

    public bool RequestModuleSwitch(bool allowWhileNavigationBusy = false)
    {
        if (SwitchModuleVisibility != Visibility.Visible || (IsBusy && !allowWhileNavigationBusy))
            return false;
        ModuleSwitchRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    public void MarkInitialLoadComplete()
    {
        _isInitialLoadComplete = true;
        Notify(nameof(IsInitialLoadComplete));
    }

    public void RestoreProcessSelection(ExpirationsProcessOption option)
    {
        _isProcessSelectionChanging = true;
        try
        {
            _selectedProcessOption = option;
            Notify(nameof(SelectedProcessOption));
        }
        finally
        {
            _isProcessSelectionChanging = false;
        }
    }

    public void LoadEmailSettings(ExpirationsEmailSettingsSnapshot snapshot)
    {
        _emailSettingsSnapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _subject = snapshot.Settings.DefaultSubject;
        _message = snapshot.Settings.DefaultMessage;
        _commonCcText = string.Join(Environment.NewLine, snapshot.Settings.CommonCcAddresses);
        _persistedSubject = _subject;
        _persistedMessage = _message;
        _persistedCommonCcText = _commonCcText;
        NotifyAllState();
    }

    public void MarkEditableConfigurationSaved(ExpirationsEmailSettingsSnapshot snapshot)
    {
        LoadEmailSettings(snapshot);
        _persistedSignatureImagePath = _signatureImagePath;
        NotifyAllState();
    }

    public void LoadPersistedSignature(string? path, BitmapSource? preview, string stateText)
    {
        _signatureImagePath = path;
        _persistedSignatureImagePath = path;
        _signaturePreview = preview;
        _signatureStateText = stateText;
        NotifyAllState();
    }

    public void SetSignature(string? path, BitmapSource? preview, string stateText)
    {
        _signatureImagePath = path;
        _signaturePreview = preview;
        _signatureStateText = stateText;
        InvalidateSendPreview();
        NotifyAllState();
    }

    public void ApplySnapshot(ExpirationsAnalysisSessionSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _generationBatch = null;
        ClearSendState();
        if (snapshot.Process is { } process)
            _selectedProcessOption = ProcessOptions.Single(option => option.Value == process);
        _selectedPendingIssue = null;
        RebuildBrokerRows();
        NotifyAllState();
    }

    public void ResetTransientState(ExpirationsAnalysisSessionSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _generationBatch = null;
        _sendPreparation = null;
        _sendResult = null;
        _eligibleBrokerIds.Clear();
        _selectedBrokerIds.Clear();
        _sendStatuses.Clear();
        _selectedPendingIssue = null;
        _selectedMonthOption = null;
        _nextMonthYearText = string.Empty;
        _premiumColumnOptions = null;
        _nextMonthGenerationReady = false;
        _premiumColumnSelectionRequired = false;
        _generationReadinessText = "Seleccione un mes y año para validar la generación.";
        RebuildBrokerRows();
        NotifyAllState();
    }

    public void ApplyGenerationBatch(ExpirationsGenerationBatch batch)
    {
        _generationBatch = batch ?? throw new ArgumentNullException(nameof(batch));
        ClearSendState();
        _eligibleBrokerIds.Clear();
        _selectedBrokerIds.Clear();
        _sendStatuses.Clear();
        _resetSelectionOnNextPreparation = true;
        RebuildBrokerRows();
        NotifyAllState();
    }

    public void ReplaceGenerationBatch(ExpirationsGenerationBatch batch)
    {
        _generationBatch = batch ?? throw new ArgumentNullException(nameof(batch));
        _sendPreparation = null;
        _sendResult = null;
        RebuildBrokerRows();
        NotifyAllState();
    }

    public void ApplySendPreparation(ExpirationsSendPreparationResult preparation)
    {
        _sendPreparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _eligibleBrokerIds.Clear();
        _eligibleBrokerIds.UnionWith(preparation.EligibleBrokerIds);
        if (_resetSelectionOnNextPreparation)
        {
            _selectedBrokerIds.Clear();
            _selectedBrokerIds.UnionWith(_eligibleBrokerIds);
            _resetSelectionOnNextPreparation = false;
        }
        else
        {
            _selectedBrokerIds.IntersectWith(_snapshot.Catalog
                .Where(broker => broker.IsActive)
                .Select(broker => broker.BrokerId));
        }
        RebuildBrokerRows();
        NotifyAllState();
    }

    public void InvalidateSendPreview()
    {
        _sendPreparation = null;
        RebuildBrokerRows();
        NotifyAllState();
    }

    public void ApplySendResult(ExpirationsSendExecutionResult result)
    {
        _sendResult = result ?? throw new ArgumentNullException(nameof(result));
        foreach (var item in result.Items)
            _sendStatuses[item.BrokerId] = item;
        RebuildBrokerRows();
        NotifyAllState();
    }

    public bool TryGetNextMonthPeriod(out ExpirationsPeriod? period)
    {
        period = null;
        if (SelectedMonthOption is null ||
            !int.TryParse(NextMonthYearText, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            return false;
        try
        {
            period = new ExpirationsPeriod(year, SelectedMonthOption.Month);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public void SetPremiumColumnOptions(ExpirationsPremiumColumnOptions options)
    {
        _premiumColumnOptions = options ?? throw new ArgumentNullException(nameof(options));
        _generationBatch = null;
        ClearSendState();
        RebuildBrokerRows();
        InvalidateGenerationReadiness("Validando las columnas Prima y Moneda seleccionadas...");
    }

    public void ResetPremiumColumnOptions()
    {
        _premiumColumnOptions = null;
        _generationBatch = null;
        ClearSendState();
        RebuildBrokerRows();
        InvalidateGenerationReadiness("Seleccione un mes y año para validar la generación.");
    }

    public void InvalidateGenerationReadiness(string message) => SetGenerationReadiness(false, message, false);

    public void SetGenerationReadiness(bool isReady, string message, bool premiumColumnSelectionRequired)
    {
        _nextMonthGenerationReady = isReady;
        _generationReadinessText = message ?? string.Empty;
        _premiumColumnSelectionRequired = premiumColumnSelectionRequired;
        NotifyAllState();
    }

    public void SetBusy(bool value, string? operationStatus = null)
    {
        if (_isBusy == value && operationStatus is null) return;
        _isBusy = value;
        _operationStatusText = value ? operationStatus ?? _operationStatusText : "Listo.";
        NotifyAllState();
    }

    public void SetOperationStatus(string value)
    {
        _operationStatusText = value;
        Notify(nameof(OperationStatusText));
    }

    private void ClearSendState()
    {
        _sendPreparation = null;
        _sendResult = null;
        _eligibleBrokerIds.Clear();
        _selectedBrokerIds.Clear();
        _sendStatuses.Clear();
        _resetSelectionOnNextPreparation = false;
    }

    private void SelectAllEligible(bool selected)
    {
        _isBulkSelectionUpdate = true;
        try
        {
            _selectedBrokerIds.ExceptWith(_eligibleBrokerIds);
            if (selected)
                _selectedBrokerIds.UnionWith(_eligibleBrokerIds);
            foreach (var row in _brokerRows)
                row.SetSelected(_selectedBrokerIds.Contains(row.BrokerId));
        }
        finally
        {
            _isBulkSelectionUpdate = false;
        }
        NotifySelectionState();
    }

    private void BrokerSelectionChanged(ExpirationsBrokerRow row)
    {
        if (_isBulkSelectionUpdate)
            return;
        if (row.CanSelectForSend && row.IsSelected)
            _selectedBrokerIds.Add(row.BrokerId);
        else
            _selectedBrokerIds.Remove(row.BrokerId);
        NotifySelectionState();
    }

    private void NotifySelectionState()
    {
        Notify(nameof(AreAllEligibleSelected));
        Notify(nameof(CanSendSelected));
        Notify(nameof(CanSendAll));
        Notify(nameof(CanReviewAndSend));
    }

    private void RebuildBrokerRows()
    {
        var distribution = _snapshot.Distribution.ToDictionary(item => item.BrokerId);
        var activeBrokerIds = _snapshot.Catalog
            .Where(item => item.IsActive)
            .Select(item => item.BrokerId)
            .ToHashSet();
        if (_generationBatch is null)
            _selectedBrokerIds.Clear();
        else
            _selectedBrokerIds.IntersectWith(activeBrokerIds);
        _brokerRows.Clear();
        foreach (var broker in _snapshot.Catalog.Where(item => item.IsActive)
                     .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            distribution.TryGetValue(broker.BrokerId, out var preview);
            var isParticipant = _generationBatch is not null &&
                (_generationBatch.ParticipatingBrokerIds.Count > 0
                    ? _generationBatch.ParticipatingBrokerIds.Contains(broker.BrokerId)
                    : preview is not null);
            _sendStatuses.TryGetValue(broker.BrokerId, out var sendResult);
            var files = (_generationBatch?.Files ?? []).Where(file => file.BrokerId == broker.BrokerId)
                .OrderBy(file => file.Variant).ToList();
            var warnings = files.SelectMany(file => file.Warnings).Distinct(StringComparer.Ordinal).ToList();
            if (_generationBatch is not null && isParticipant && files.Count == 0)
                warnings.Add("Falta el archivo obligatorio del batch actual.");
            if (warnings.Count == 0 && preview is not null && PendingItems.Count > 0)
                warnings.Add("El análisis contiene elementos pendientes de resolver.");
            if (warnings.Count == 0 && files.Count > 0 && _sendPreparation is { CanSend: false })
            {
                var knownPrefixes = _snapshot.Catalog.Select(item => $"{item.Name}: ").ToList();
                var ownPrefix = $"{broker.Name}: ";
                warnings.AddRange(_sendPreparation.Errors.Where(error =>
                    error.StartsWith(ownPrefix, StringComparison.CurrentCultureIgnoreCase) ||
                    !knownPrefixes.Any(prefix => error.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))));
            }

            var status = sendResult switch
            {
                { IsConfirmed: true, WasSuccessful: true } => "Enviado",
                { } => "Fallido",
                _ when files.Count > 0 && warnings.Count > 0 => "Con advertencia",
                _ when _eligibleBrokerIds.Contains(broker.BrokerId) => "Pendiente / No enviado",
                _ when _generationBatch is not null && !isParticipant &&
                    files.Any(file => file.Variant == ExpirationsGeneratedFileVariant.Manual) =>
                    "Archivo manual asociado",
                _ when files.Count > 0 => "Generado",
                _ when _generationBatch is not null && isParticipant => "Con advertencia",
                _ when _snapshot.Analysis is null => "Sin analizar",
                _ when preview is null => "No participa en el Excel",
                _ when PendingItems.Count > 0 => "Pendiente de resolver",
                _ when _snapshot.Analysis.CanGenerate => "Listo para generar",
                _ => "Con advertencia"
            };
            _brokerRows.Add(new ExpirationsBrokerRow(
                broker.BrokerId,
                broker.Name,
                string.Join("; ", broker.PrimaryEmailAddresses),
                string.Join("; ", broker.Assistants.Where(item => item.IsActive)
                    .Select(item => $"{item.Name} — {item.Email}")),
                preview?.RowCount ?? 0,
                files,
                status,
                string.Join(Environment.NewLine, warnings),
                _eligibleBrokerIds.Contains(broker.BrokerId),
                _selectedBrokerIds.Contains(broker.BrokerId),
                _generationBatch is not null,
                _generationBatch is not null,
                isParticipant,
                BrokerSelectionChanged));
        }
        _brokerRowsView.Refresh();
        NotifySelectionState();
    }

    private bool FilterBrokerRow(object value)
    {
        if (value is not ExpirationsBrokerRow row || string.IsNullOrWhiteSpace(_searchText))
            return true;
        var query = _searchText.Trim();
        return row.BrokerName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            row.PrimaryEmail.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.AssistantsText.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SetEditableField(ref string field, string value, [CallerMemberName] string? propertyName = null)
    {
        if (string.Equals(field, value, StringComparison.Ordinal)) return;
        field = value;
        InvalidateSendPreview();
        Notify(propertyName);
        Notify(nameof(HasEditableChanges));
        Notify(nameof(CanSave));
        Notify(nameof(EmailSettingsStateText));
    }

    private static bool PathValuesEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void NotifyAllState() => Notify(string.Empty);
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed class ExpirationsBrokerRow : INotifyPropertyChanged
{
    private readonly Action<ExpirationsBrokerRow> _selectionChanged;
    private bool _isSelected;

    public ExpirationsBrokerRow(
        Guid brokerId,
        string brokerName,
        string primaryEmail,
        string assistantsText,
        int rowCount,
        IReadOnlyList<ExpirationsGeneratedFile> generatedFiles,
        string statusText,
        string warningText,
        bool isEligible,
        bool isSelected,
        bool canManageFiles,
        bool canSelectForSend,
        bool isParticipant,
        Action<ExpirationsBrokerRow> selectionChanged)
    {
        BrokerId = brokerId;
        BrokerName = brokerName;
        PrimaryEmail = primaryEmail;
        AssistantsText = assistantsText;
        RowCount = rowCount;
        GeneratedFiles = generatedFiles;
        StatusText = statusText;
        WarningText = warningText;
        IsEligible = isEligible;
        CanSelectForSend = canSelectForSend;
        IsParticipant = isParticipant;
        _isSelected = canSelectForSend && isSelected;
        CanManageFiles = canManageFiles;
        _selectionChanged = selectionChanged;
    }

    public Guid BrokerId { get; }
    public string BrokerName { get; }
    public string PrimaryEmail { get; }
    public string AssistantsText { get; }
    public int RowCount { get; }
    public IReadOnlyList<ExpirationsGeneratedFile> GeneratedFiles { get; }
    public string StatusText { get; }
    public string WarningText { get; }
    public bool IsEligible { get; }
    public bool CanManageFiles { get; }
    public bool CanSelectForSend { get; }
    public bool IsParticipant { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            var normalized = CanSelectForSend && value;
            if (_isSelected == normalized)
                return;
            _isSelected = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            _selectionChanged(this);
        }
    }

    public void SetSelected(bool value)
    {
        var normalized = CanSelectForSend && value;
        if (_isSelected == normalized)
            return;
        _isSelected = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
    }

    public string FileCountText => GeneratedFiles.Count == 1 ? "1 archivo" : $"{GeneratedFiles.Count} archivos";
    public bool CanViewFiles => CanManageFiles;
    public string SelectionHint => IsEligible
        ? "Incluye este corredor en Enviar seleccionados."
        : !CanSelectForSend
            ? "Genere un batch antes de seleccionar este corredor."
            : !IsParticipant && GeneratedFiles.All(file => file.Variant != ExpirationsGeneratedFileVariant.Manual)
                ? "Agregue al menos un archivo manual antes de enviarlo."
                : IsParticipant
                    ? "El corredor no tiene todos sus archivos obligatorios válidos."
                    : "Incluye este corredor en Enviar seleccionados para un envío manual.";

    public event PropertyChangedEventHandler? PropertyChanged;
}
