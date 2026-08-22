using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using Microsoft.Win32;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer.Views;

public partial class ExpirationsWindow : Window
{
    private readonly IAppUserRepository _appUsers;
    private readonly IExpirationsAnalysisCoordinator _coordinator;
    private readonly IExpirationsBrokerConfigurationService _configurationService;
    private readonly IExpirationsGenerationService _generationService;
    private readonly ExpirationsWindowState _state;
    private int _readinessVersion;
    private CancellationTokenSource? _readinessCancellation;

    public ExpirationsWindow(
        AppUser currentUser,
        IAppUserRepository appUsers,
        IExpirationsAnalysisCoordinator coordinator,
        IExpirationsBrokerConfigurationService configurationService,
        IExpirationsGenerationService generationService)
    {
        ArgumentNullException.ThrowIfNull(appUsers);
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _generationService = generationService ?? throw new ArgumentNullException(nameof(generationService));
        _state = new ExpirationsWindowState(currentUser);
        _appUsers = appUsers;
        InitializeComponent();
        DataContext = _state;
        _state.ApplySnapshot(_coordinator.Snapshot);
    }

    public event EventHandler? LogoutRequested
    {
        add => _state.LogoutRequested += value;
        remove => _state.LogoutRequested -= value;
    }

    private async void ProcessComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _coordinator.SelectProcess(_state.SelectedProcessOption?.Value);
        _state.ApplySnapshot(_coordinator.Snapshot);
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

    private async void ConfigureBrokers_Click(object sender, RoutedEventArgs e)
    {
        var management = new ExpirationsBrokerManagementWindow(_configurationService) { Owner = this };
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
            MessageBox.Show(
                $"Generación completada.\n\nCorredores con archivos: {batch.Files.Select(file => file.BrokerId).Distinct().Count()}\nArchivos generados: {batch.Files.Count}\nCarpeta: {batch.OutputDirectory}",
                "Generación de Vencimientos",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        });
    }

    private void OpenGeneratedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_state.GeneratedOutputDirectory.Length == 0 || !Directory.Exists(_state.GeneratedOutputDirectory))
            return;
        Process.Start(new ProcessStartInfo
        {
            FileName = _state.GeneratedOutputDirectory,
            UseShellExecute = true
        });
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

    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "¿Desea cerrar la sesión de Firebase en este equipo?",
                "Cerrar sesión",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _state.RequestLogout();
        Close();
    }
}

internal sealed class ExpirationsWindowState : INotifyPropertyChanged
{
    private ExpirationsProcessOption? _selectedProcessOption;
    private ExpirationsAnalysisSessionSnapshot _snapshot = new();
    private ExpirationsPendingIssue? _selectedPendingIssue;
    private bool _isBusy;
    private string _operationStatusText = string.Empty;
    private ExpirationsGenerationBatch? _generationBatch;
    private ExpirationsMonthOption? _selectedMonthOption;
    private string _nextMonthYearText = string.Empty;
    private ExpirationsPremiumColumnOptions? _premiumColumnOptions;
    private bool _nextMonthGenerationReady;
    private bool _premiumColumnSelectionRequired;
    private string _generationReadinessText = "Seleccione un mes y año para validar la generación.";

    public ExpirationsWindowState(AppUser currentUser)
    {
        AppUserAuthorization.DemandExpirationsAccess(currentUser);
        CurrentUser = currentUser;
        ProcessOptions =
        [
            new ExpirationsProcessOption(
                ExpirationsProcess.PreviousMonth,
                "Pendientes del mes anterior"),
            new ExpirationsProcessOption(
                ExpirationsProcess.NextMonth,
                "Vencimientos del mes siguiente")
        ];
        MonthOptions = Enumerable.Range(1, 12)
            .Select(month => new ExpirationsMonthOption(
                month,
                CultureInfo.GetCultureInfo("es-CR").TextInfo.ToTitleCase(
                    CultureInfo.GetCultureInfo("es-CR").DateTimeFormat.GetMonthName(month))))
            .ToArray();
    }

    public AppUser CurrentUser { get; }
    public IReadOnlyList<ExpirationsProcessOption> ProcessOptions { get; }
    public IReadOnlyList<ExpirationsMonthOption> MonthOptions { get; }
    public string SignedInUserText => $"{CurrentUser.DisplayName} · {CurrentUser.Email}";
    public Visibility AdminAccessVisibility =>
        CurrentUser.IsActive && CurrentUser.Role == AppUserRole.Admin
            ? Visibility.Visible
            : Visibility.Collapsed;
    public string SourcePath => _snapshot.SourcePath;
    public bool IsBusy => _isBusy;
    public bool CanSelectFile => !IsBusy;
    public bool CanConfigureBrokers => !IsBusy;
    public bool CanAnalyze => !IsBusy && SelectedProcessOption is not null && SourcePath.Length > 0;
    public bool CanResolve => !IsBusy && SelectedPendingIssue?.CanResolve == true;
    public bool CanSelectPremiumColumns => !IsBusy &&
        _snapshot.Process == ExpirationsProcess.NextMonth &&
        _snapshot.ReadResult?.Workbook is not null;
    public bool CanGenerate => !IsBusy &&
        _snapshot.Analysis is { CanGenerate: true, TotalRows: > 0 } &&
        _snapshot.Distribution.Count > 0 &&
        (_snapshot.Process == ExpirationsProcess.PreviousMonth ||
         (_snapshot.Process == ExpirationsProcess.NextMonth && _nextMonthGenerationReady));
    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public string OperationStatusText => _operationStatusText;
    public Visibility ResultVisibility =>
        _snapshot.Analysis is not null || _snapshot.ReadResult is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility AnalysisVisibility =>
        _snapshot.Analysis is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingVisibility =>
        PendingItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReadyVisibility =>
        _snapshot.Analysis is not null && PendingItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility GenerationVisibility => AnalysisVisibility;
    public Visibility GenerationResultVisibility =>
        _generationBatch is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NextMonthPeriodVisibility =>
        SelectedProcessOption?.Value == ExpirationsProcess.NextMonth
            ? Visibility.Visible
            : Visibility.Collapsed;
    public Visibility PremiumColumnSelectionVisibility =>
        _premiumColumnSelectionRequired ? Visibility.Visible : Visibility.Collapsed;
    public string GenerationReadinessText => _generationReadinessText;
    public int TotalRows => _snapshot.Analysis?.TotalRows ?? 0;
    public int ResolvedRows => _snapshot.Analysis?.ResolvedRows ?? 0;
    public int PendingRows => _snapshot.Analysis?.RowsWithBlockingIssues ?? 0;
    public int DestinationCount => PreviewItems.Count;
    public IReadOnlyList<ExpirationsDistributionPreviewItem> PreviewItems => _snapshot.Distribution;
    public IReadOnlyList<ExpirationsPendingIssue> PendingItems => _snapshot.PendingIssues;
    public string StatusText => _snapshot.Analysis switch
    {
        { CanGenerate: true } => "Análisis completo.",
        { } when PendingItems.Count == 1 => "El análisis tiene 1 elemento pendiente de revisión.",
        { } => $"El análisis tiene {PendingItems.Count} elementos pendientes de revisión.",
        _ when _snapshot.RequiresWorkbookSelection => "No se identificó automáticamente la columna Corredor.",
        _ => "No fue posible completar el análisis."
    };
    public string StatusDetailText => _snapshot.Analysis?.CanGenerate == true
        ? _snapshot.Process == ExpirationsProcess.PreviousMonth
            ? "Todas las pólizas tienen un corredor identificado. Listo para la generación."
            : "Todas las pólizas tienen un corredor identificado."
        : _snapshot.Messages.Count > 0
            ? string.Join(" ", _snapshot.Messages)
            : "Revise los elementos pendientes antes de continuar.";
    public string ReadyText => _snapshot.Process == ExpirationsProcess.PreviousMonth
        ? "El análisis está completo y listo para la generación."
        : "El análisis está completo.";
    public int GeneratedFileCount => _generationBatch?.Files.Count ?? 0;
    public int GeneratedBrokerCount => _generationBatch?.Files.Select(file => file.BrokerId).Distinct().Count() ?? 0;
    public string GeneratedOutputDirectory => _generationBatch?.OutputDirectory ?? string.Empty;
    public string GenerationWarningsText => _generationBatch is { Warnings.Count: > 0 }
        ? string.Join(Environment.NewLine, _generationBatch.Warnings)
        : "Sin advertencias.";
    public ExpirationsPremiumColumnOptions? PremiumColumnOptions => _premiumColumnOptions;

    public ExpirationsMonthOption? SelectedMonthOption
    {
        get => _selectedMonthOption;
        set
        {
            if (Equals(_selectedMonthOption, value)) return;
            _selectedMonthOption = value;
            _generationBatch = null;
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
            InvalidateGenerationReadiness(
                "Seleccione un mes y año para validar la configuración especial y las primas.");
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
        }
    }

    public event EventHandler? LogoutRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void RequestLogout() => LogoutRequested?.Invoke(this, EventArgs.Empty);

    public void ApplySnapshot(ExpirationsAnalysisSessionSnapshot snapshot)
    {
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _generationBatch = null;
        if (snapshot.Process is { } process)
            _selectedProcessOption = ProcessOptions.Single(option => option.Value == process);
        _selectedPendingIssue = null;
        NotifyAllState();
    }

    public void ApplyGenerationBatch(ExpirationsGenerationBatch batch)
    {
        _generationBatch = batch ?? throw new ArgumentNullException(nameof(batch));
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
        InvalidateGenerationReadiness("Validando las columnas Prima y Moneda seleccionadas...");
    }

    public void ResetPremiumColumnOptions()
    {
        _premiumColumnOptions = null;
        _generationBatch = null;
        InvalidateGenerationReadiness("Seleccione un mes y año para validar la generación.");
    }

    public void InvalidateGenerationReadiness(string message) =>
        SetGenerationReadiness(false, message, false);

    public void SetGenerationReadiness(
        bool isReady,
        string message,
        bool premiumColumnSelectionRequired)
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
        _operationStatusText = value ? operationStatus ?? _operationStatusText : string.Empty;
        NotifyAllState();
    }

    public void SetOperationStatus(string value)
    {
        _operationStatusText = value;
        Notify(nameof(OperationStatusText));
    }

    private void NotifyAllState() => Notify(string.Empty);
    private void Notify([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
