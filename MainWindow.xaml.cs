using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;
using ECS.CommissionsMailer.Views;
using Microsoft.Win32;
using MessageBox = ECS.CommissionsMailer.Views.AppDialog;

namespace ECS.CommissionsMailer;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly AppDataPaths _paths;
    private readonly FileLogger _logger;
    private readonly ConfigurationService _configurationService;
    private readonly SessionService _sessionService;
    private readonly EmailValidationService _validationService = new();
    private readonly SignatureImageService _signatureService;
    private readonly AttachmentArchiveService _archiveService;
    private readonly OutlookEmailService _outlookService;
    private readonly WorksheetBrokerMappingService _worksheetMappingService = new();
    private readonly WorkbookAnalysisService _workbookAnalysisService;
    private readonly FileNameSanitizer _fileNameSanitizer = new();
    private readonly DesktopOutputDirectoryService _desktopOutputService = new();
    private readonly GenerationHistoryService _generationHistoryService;
    private readonly PaymentWorkbookGenerationService _paymentWorkbookGenerationService;
    private readonly GeneratedFileViewerService _generatedFileViewerService;
    private readonly GeneratedBrokerSelectionService _generatedBrokerSelectionService;
    private readonly ObservableCollection<SentEmailRecord> _recentRecords;
    private readonly List<PaymentGenerationBatch> _paymentGenerationHistory;
    private readonly bool _isUiSmokeTest;
    private readonly ICollectionView _brokerItemsView;
    private AppConfiguration _configuration;
    private string _subject = string.Empty;
    private string _message = string.Empty;
    private string _commonCcText = string.Empty;
    private BrokerSendItem? _selectedBrokerItem;
    private bool _isBusy;
    private string _operationText = "Listo.";
    private int _progressMaximum = 1;
    private int _progressValue;
    private string _outlookStatusText = "Comprobando Outlook...";
    private Brush _outlookStatusBrush = Brushes.Goldenrod;
    private string _outlookAccountEmail = "Cuenta no conectada";
    private bool _loaded;
    private string _searchText = string.Empty;
    private BitmapSource? _signaturePreview;
    private string _signatureFileName = string.Empty;
    private string _signatureStateText = "No hay una firma configurada.";
    private string? _signatureLoadWarning;
    private bool _isBulkSelectionUpdate;
    private string _generalWorkbookPath = string.Empty;
    private string _workbookAnalysisText = "No hay un Excel general cargado.";
    private string _generationStatusText = "Todavía no se han generado archivos para esta sesión.";
    private string _generatedOutputDirectory = string.Empty;
    private WorkbookAnalysisResult? _currentWorkbookAnalysis;
    private PaymentGenerationBatch? _activePaymentGeneration;

    public MainWindow(AppDataPaths paths, FileLogger logger, bool isUiSmokeTest = false)
    {
        InitializeComponent();
        _paths = paths;
        _logger = logger;
        _isUiSmokeTest = isUiSmokeTest;
        _configurationService = new ConfigurationService(paths, logger);
        _sessionService = new SessionService(paths, logger);
        _signatureService = new SignatureImageService(paths);
        _archiveService = new AttachmentArchiveService(paths, logger);
        _outlookService = new OutlookEmailService(logger);
        var hashService = new GeneratedFileHashService();
        _workbookAnalysisService = new WorkbookAnalysisService(hashService: hashService);
        _generationHistoryService = new GenerationHistoryService(paths, logger);
        _generatedFileViewerService = new GeneratedFileViewerService(
            new GeneratedFileProcessLauncher(),
            logger);
        _generatedBrokerSelectionService = new GeneratedBrokerSelectionService(logger);
        _paymentWorkbookGenerationService = new PaymentWorkbookGenerationService(
            new PaymentCalculationService(),
            _fileNameSanitizer,
            hashService,
            _generationHistoryService,
            logger);

        _configuration = _configurationService.Load();
        var session = _sessionService.LoadCurrent();
        _recentRecords = new ObservableCollection<SentEmailRecord>(_sessionService.LoadRecentSends());
        _paymentGenerationHistory = _generationHistoryService.Load();
        _activePaymentGeneration = _generationHistoryService.FindActive(
            _paymentGenerationHistory,
            session?.ActivePaymentGenerationId);
        _generalWorkbookPath = session?.GeneralWorkbookPath ?? string.Empty;
        _generatedOutputDirectory = session?.GeneratedOutputDirectory ?? string.Empty;
        if (_activePaymentGeneration is not null)
        {
            _generationStatusText =
                $"{_activePaymentGeneration.Period}: {_activePaymentGeneration.Files.Count} archivo(s) listos.";
        }
        else if (!string.IsNullOrWhiteSpace(session?.GeneratedPeriod))
        {
            _generationStatusText = $"{session.GeneratedPeriod}: la generación ya no está disponible.";
        }

        if (!string.IsNullOrWhiteSpace(_generalWorkbookPath))
        {
            _workbookAnalysisText = File.Exists(_generalWorkbookPath)
                ? $"Cargado: {Path.GetFileName(_generalWorkbookPath)}. Se analizará antes de generar."
                : "El Excel general guardado ya no existe.";
        }
        _brokerItemsView = CollectionViewSource.GetDefaultView(BrokerItems);
        _brokerItemsView.Filter = FilterBrokerItem;
        Subject = string.IsNullOrEmpty(session?.Subject) ? _configuration.DefaultSubject : session.Subject;
        Message = string.IsNullOrEmpty(session?.Message) ? _configuration.DefaultMessage : session.Message;
        CommonCcText = string.IsNullOrWhiteSpace(session?.CommonCcText)
            ? string.Join(Environment.NewLine, _configuration.CommonCcAddresses)
            : session.CommonCcText;
        ReloadBrokerRows(session?.BrokerItems);
        LoadSignaturePreview();
        DataContext = this;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<BrokerSendItem> BrokerItems { get; } = [];
    public ObservableCollection<Broker> InactiveBrokers { get; } = [];
    public ICollectionView BrokerItemsView => _brokerItemsView;

    public string GeneralWorkbookPath
    {
        get => _generalWorkbookPath;
        private set
        {
            if (SetProperty(ref _generalWorkbookPath, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(GeneralWorkbookFileName));
            }
        }
    }

    public string GeneralWorkbookFileName => string.IsNullOrWhiteSpace(GeneralWorkbookPath)
        ? "Ningún archivo seleccionado"
        : Path.GetFileName(GeneralWorkbookPath);

    public string WorkbookAnalysisText
    {
        get => _workbookAnalysisText;
        private set => SetProperty(ref _workbookAnalysisText, value);
    }

    public string GenerationStatusText
    {
        get => _generationStatusText;
        private set => SetProperty(ref _generationStatusText, value);
    }

    public string GeneratedOutputDirectory
    {
        get => _generatedOutputDirectory;
        private set
        {
            if (SetProperty(ref _generatedOutputDirectory, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(CanOpenGeneratedFolder));
            }
        }
    }

    public bool CanOpenGeneratedFolder =>
        !string.IsNullOrWhiteSpace(GeneratedOutputDirectory) && Directory.Exists(GeneratedOutputDirectory);

    public bool HasActivePaymentGeneration => _activePaymentGeneration is not null;

    public string Subject
    {
        get => _subject;
        set => SetProperty(ref _subject, value ?? string.Empty);
    }

    public string Message
    {
        get => _message;
        set => SetProperty(ref _message, value ?? string.Empty);
    }

    public string CommonCcText
    {
        get => _commonCcText;
        set => SetProperty(ref _commonCcText, value ?? string.Empty);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value ?? string.Empty))
            {
                _brokerItemsView.Refresh();
                OnPropertyChanged(nameof(BrokerSummaryText));
            }
        }
    }

    public BitmapSource? SignaturePreview
    {
        get => _signaturePreview;
        private set => SetProperty(ref _signaturePreview, value);
    }

    public string SignatureFileName
    {
        get => _signatureFileName;
        private set => SetProperty(ref _signatureFileName, value);
    }

    public string SignatureStateText
    {
        get => _signatureStateText;
        private set => SetProperty(ref _signatureStateText, value);
    }

    public bool HasSignature => SignaturePreview is not null;
    public bool HasConfiguredSignature => !string.IsNullOrWhiteSpace(_configuration.SignatureImagePath);
    public Visibility SignaturePreviewVisibility => HasSignature ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SignatureEmptyVisibility => HasSignature ? Visibility.Collapsed : Visibility.Visible;
    public string SignatureSelectButtonText => string.IsNullOrWhiteSpace(_configuration.SignatureImagePath)
        ? "Seleccionar imagen"
        : "Cambiar imagen";

    public BrokerSendItem? SelectedBrokerItem
    {
        get => _selectedBrokerItem;
        set
        {
            if (SetProperty(ref _selectedBrokerItem, value))
            {
                OnPropertyChanged(nameof(IsSelectedBrokerActive));
            }
        }
    }

    public bool IsUiEnabled => !_isBusy;
    public bool CanSendSelected => IsUiEnabled && BrokerItems.Any(item => item.IsSelected);
    public bool IsSelectedBrokerActive => SelectedBrokerItem is not null &&
        _configuration.Brokers.FirstOrDefault(value => value.Id == SelectedBrokerItem.BrokerId)?.IsActive == true;
    public string InactiveBrokersButtonText => $"Ver inactivos ({InactiveBrokers.Count})";
    public bool? AreAllBrokersSelected
    {
        get
        {
            if (BrokerItems.Count == 0 || BrokerItems.All(item => !item.IsSelected))
            {
                return false;
            }

            return BrokerItems.All(item => item.IsSelected) ? true : null;
        }
    }

    public string BrokerSummaryText
    {
        get
        {
            var visible = _brokerItemsView.Cast<object>().Count();
            return string.IsNullOrWhiteSpace(SearchText)
                ? $"{BrokerItems.Count} corredor(es) activo(s)"
                : $"{visible} de {BrokerItems.Count} corredor(es)";
        }
    }

    public string OperationText
    {
        get => _operationText;
        set => SetProperty(ref _operationText, value);
    }

    public int ProgressMaximum
    {
        get => _progressMaximum;
        set => SetProperty(ref _progressMaximum, Math.Max(1, value));
    }

    public int ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public string OutlookStatusText
    {
        get => _outlookStatusText;
        set => SetProperty(ref _outlookStatusText, value);
    }

    public Brush OutlookStatusBrush
    {
        get => _outlookStatusBrush;
        set => SetProperty(ref _outlookStatusBrush, value);
    }

    public string OutlookAccountEmail
    {
        get => _outlookAccountEmail;
        set => SetProperty(ref _outlookAccountEmail, value);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(_configurationService.LastWarning))
        {
            warnings.Add(_configurationService.LastWarning);
        }

        warnings.AddRange(_sessionService.Warnings);
        warnings.AddRange(_generationHistoryService.Warnings);
        if (!string.IsNullOrWhiteSpace(_signatureLoadWarning))
        {
            warnings.Add(_signatureLoadWarning);
        }
        if (!_isUiSmokeTest && warnings.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine + Environment.NewLine, warnings),
                "Recuperación de datos", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (!_isUiSmokeTest && BrokerItems.Count == 0)
        {
            MessageBox.Show("Aún no hay corredores configurados. Use 'Agregar corredor' para comenzar.",
                "Primer uso", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        var availability = await RefreshOutlookConnectionAsync();
        if (_isUiSmokeTest)
        {
            var snapshotPath = Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-ui-smoke.png");
            var minimumSnapshotPath = Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-ui-smoke-minimum.png");
            RenderUiSnapshot(snapshotPath);
            Width = MinWidth;
            Height = MinHeight;
            RenderUiSnapshot(minimumSnapshotPath);
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), "ECSCommissionsMailer-ui-smoke-result.txt"),
                $"VENTANA_INICIADA=SI{Environment.NewLine}OUTLOOK_DISPONIBLE={(availability.Available ? "SI" : "NO")}{Environment.NewLine}ESTADO={availability.Message}{Environment.NewLine}CAPTURA={snapshotPath}{Environment.NewLine}CAPTURA_MINIMA={minimumSnapshotPath}");
            Close();
        }
    }

    private async void ConnectOutlook_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        OperationText = "Conectando con Outlook...";
        try
        {
            var result = await RefreshOutlookConnectionAsync();
            OperationText = result.Available
                ? "Cuenta de Outlook conectada."
                : "No fue posible conectar la cuenta de Outlook.";
            MessageBox.Show(
                result.Message,
                "Conexión con Outlook",
                MessageBoxButton.OK,
                result.Available ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _logger.Error("Error inesperado al conectar con Outlook.", ex);
            OutlookAccountEmail = "Cuenta no conectada";
            OutlookStatusBrush = Brushes.IndianRed;
            OperationText = "No fue posible conectar la cuenta de Outlook.";
            MessageBox.Show(
                $"No fue posible conectar con Outlook.{Environment.NewLine}" +
                $"Consulte el registro técnico en: {_logger.LogFilePath}",
                "Conexión con Outlook",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task<OutlookConnectionInfo> RefreshOutlookConnectionAsync()
    {
        var connection = await _outlookService.CheckAvailabilityAsync();
        OutlookStatusText = connection.Message;
        OutlookStatusBrush = connection.Available ? Brushes.SeaGreen : Brushes.IndianRed;
        OutlookAccountEmail = connection.Available
            ? connection.EmailAddress ?? "Correo no identificado"
            : "Cuenta no conectada";
        return connection;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_isBusy)
        {
            e.Cancel = true;
            MessageBox.Show("Espere a que finalice el envío antes de cerrar la aplicación.",
                "Envío en curso", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            SaveCurrentSession();
            _logger.Info("Cierre de ECS Envío de Correos.");
        }
        catch (Exception ex)
        {
            _logger.Error("No se pudo guardar la sesión al cerrar.", ex);
            MessageBox.Show($"No fue posible guardar la sesión actual.\n\n{ex.Message}",
                "Error al guardar", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddBroker_Click(object sender, RoutedEventArgs e)
    {
        var editor = new BrokerEditorWindow(
            null,
            _validationService,
            _configuration.Brokers,
            _worksheetMappingService) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        _configuration.Brokers.Add(editor.EditedBroker);
        PersistBrokerChanges();
    }

    private void EditBroker_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedItemOrWarn();
        if (item is null)
        {
            return;
        }

        var broker = _configuration.Brokers.FirstOrDefault(value => value.Id == item.BrokerId);
        if (broker is null)
        {
            MessageBox.Show("No se encontró la configuración del corredor seleccionado.",
                "Corredor", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var editor = new BrokerEditorWindow(
            broker,
            _validationService,
            _configuration.Brokers,
            _worksheetMappingService) { Owner = this };
        if (editor.ShowDialog() != true)
        {
            return;
        }

        var edited = editor.EditedBroker;
        broker.Name = edited.Name;
        broker.PrimaryEmailAddresses = edited.PrimaryEmailAddresses;
        broker.Assistants = edited.Assistants;
        broker.AssociatedWorksheetNames = edited.AssociatedWorksheetNames;
        broker.Deductions = edited.Deductions;
        broker.IsActive = edited.IsActive;
        broker.RequiresReview = edited.RequiresReview;
        broker.ReviewNote = edited.ReviewNote;
        PersistBrokerChanges();
    }

    private void DeleteBroker_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedItemOrWarn();
        if (item is null)
        {
            return;
        }

        if (MessageBox.Show($"¿Desea eliminar al corredor '{item.BrokerName}'?\n\nEsta acción no elimina los registros de envíos anteriores.",
                "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var broker = _configuration.Brokers.FirstOrDefault(value => value.Id == item.BrokerId);
        if (broker is not null)
        {
            _configuration.Brokers.Remove(broker);
        }

        PersistBrokerChanges();
    }

    private void ReloadBrokers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveCurrentSession();
            _configuration = _configurationService.Load();
            ReloadBrokerRows(BrokerItems.ToList());
            OperationText = "Corredores recargados.";
        }
        catch (Exception ex)
        {
            ShowSaveError(ex);
        }
    }

    private void ToggleBrokerActive_Click(object sender, RoutedEventArgs e)
    {
        var item = GetSelectedItemOrWarn();
        if (item is null)
        {
            (sender as ToggleButton)?.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
            return;
        }

        var broker = _configuration.Brokers.FirstOrDefault(value => value.Id == item.BrokerId);
        if (broker is null)
        {
            OnPropertyChanged(nameof(IsSelectedBrokerActive));
            return;
        }

        var shouldBeActive = (sender as ToggleButton)?.IsChecked == true;
        if (shouldBeActive == broker.IsActive)
        {
            return;
        }

        if (!shouldBeActive && MessageBox.Show($"¿Desea desactivar a '{broker.IdentityText}'?\n\nPodrá reactivarlo desde la lista de inactivos.",
                "Desactivar corredor", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            OnPropertyChanged(nameof(IsSelectedBrokerActive));
            return;
        }

        broker.IsActive = shouldBeActive;
        PersistBrokerChanges();
    }

    private void SelectSignature_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar firma del correo",
            Filter = "Imágenes permitidas (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var previousPath = _configuration.SignatureImagePath;
        string? managedPath = null;
        try
        {
            managedPath = _signatureService.Import(dialog.FileName);
            _configuration.SignatureImagePath = managedPath;
            _configurationService.Save(_configuration);
            LoadSignaturePreview();
            TryDeleteManagedSignature(previousPath);
            OperationText = "Firma del correo actualizada.";
        }
        catch (Exception ex) when (ex is SignatureImageException or IOException or UnauthorizedAccessException)
        {
            _configuration.SignatureImagePath = previousPath;
            if (!string.IsNullOrWhiteSpace(managedPath))
            {
                try { _signatureService.DeleteIfManaged(managedPath); } catch { }
            }

            _logger.Error("No fue posible actualizar la firma del correo.", ex);
            MessageBox.Show(ex.Message, "Firma del correo", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RemoveSignature_Click(object sender, RoutedEventArgs e)
    {
        var previousPath = _configuration.SignatureImagePath;
        if (string.IsNullOrWhiteSpace(previousPath))
        {
            return;
        }

        if (MessageBox.Show("¿Desea quitar la firma de todos los correos nuevos?",
                "Quitar firma", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _configuration.SignatureImagePath = null;
            _configurationService.Save(_configuration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _configuration.SignatureImagePath = previousPath;
            _logger.Error("No fue posible quitar la firma del correo.", ex);
            MessageBox.Show(ex.Message, "Firma del correo", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ClearSignaturePreview("No hay una firma configurada.");
        TryDeleteManagedSignature(previousPath);
        OperationText = "Firma del correo eliminada.";
    }

    private void ShowInactiveBrokers_Click(object sender, RoutedEventArgs e)
    {
        var window = new InactiveBrokersWindow(InactiveBrokers)
        {
            Owner = this
        };
        window.ActivationRequested += broker =>
        {
            broker.IsActive = true;
            PersistBrokerChanges();
        };
        window.ShowDialog();
    }

    private async void SelectGeneralWorkbook_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Seleccionar Excel general de comisiones",
            Filter = "Libro de Excel compatible (*.xlsx)|*.xlsx",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        GeneralWorkbookPath = Path.GetFullPath(dialog.FileName);
        await AnalyzeGeneralWorkbookAsync(showResult: true);
        SaveCurrentSessionWithMessageOnError(false);
    }

    private async void GenerateFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(GeneralWorkbookPath) || !File.Exists(GeneralWorkbookPath))
        {
            MessageBox.Show(
                "Seleccione primero el Excel general de comisiones.",
                "Generar archivos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var configurationErrors = _worksheetMappingService.ValidateConfiguration(_configuration.Brokers);
        if (configurationErrors.Count > 0)
        {
            MessageBox.Show(
                "Corrija la configuración antes de generar:\n\n" +
                string.Join(Environment.NewLine, configurationErrors.Select(value => $"• {value}")),
                "Configuración inválida", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var analysis = await AnalyzeGeneralWorkbookAsync(showResult: false);
        if (analysis is null || !analysis.IsValid)
        {
            return;
        }

        var mapping = _worksheetMappingService.Resolve(
            analysis.Worksheets.Select(value => value.WorksheetName),
            _configuration.Brokers);
        if (mapping.Errors.Count > 0)
        {
            MessageBox.Show(string.Join(Environment.NewLine, mapping.Errors.Select(value => $"• {value}")),
                "Asociaciones duplicadas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (mapping.MissingWorksheetNames.Count > 0)
        {
            var mappingWindow = new WorksheetMappingWindow(
                mapping.MissingWorksheetNames,
                _configuration.Brokers,
                _validationService,
                _worksheetMappingService)
            {
                Owner = this
            };
            if (mappingWindow.ShowDialog() != true)
            {
                OperationText = "Generación cancelada: quedaron pestañas sin asociar.";
                return;
            }

            foreach (var createdBroker in mappingWindow.CreatedBrokers)
            {
                _configuration.Brokers.Add(createdBroker);
            }

            try
            {
                _configurationService.Save(_configuration);
                ReloadBrokerRows(BrokerItems.ToList());
                SaveCurrentSession();
            }
            catch (Exception ex)
            {
                ShowSaveError(ex);
                return;
            }

            mapping = _worksheetMappingService.Resolve(
                analysis.Worksheets.Select(value => value.WorksheetName),
                _configuration.Brokers);
        }

        if (!mapping.IsValid)
        {
            MessageBox.Show(
                "No se puede generar hasta que todas las pestañas estén asociadas.",
                "Asociaciones incompletas", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var inactiveAssignments = mapping.Assignments.Where(value => !value.Broker.IsActive).ToList();
        if (inactiveAssignments.Count > 0)
        {
            MessageBox.Show(
                "Las siguientes pestañas están asociadas a corredores inactivos. Active esos corredores antes de generar:\n\n" +
                string.Join(Environment.NewLine, inactiveAssignments.Select(value =>
                    $"• {value.WorksheetName} — {value.Broker.Name}")),
                "Corredores inactivos", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var periodWindow = new GenerationPeriodWindow(
            analysis.Worksheets.Count,
            mapping.Assignments.Select(value => value.Broker.Id).Distinct().Count(),
            mapping.Assignments[0].Broker.Name,
            mapping.Assignments[0].WorksheetName,
            _desktopOutputService,
            _fileNameSanitizer)
        {
            Owner = this
        };
        if (periodWindow.ShowDialog() != true)
        {
            return;
        }

        var replaceExisting = false;
        if (periodWindow.OutputDirectoryExists)
        {
            if (MessageBox.Show(
                    $"La carpeta ya existe:\n\n{periodWindow.OutputDirectory}\n\n" +
                    "¿Desea reemplazar únicamente los archivos con los nombres de esta generación? " +
                    "Los demás archivos de la carpeta se conservarán.",
                    "Carpeta existente", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            replaceExisting = true;
        }

        SetBusy(true);
        ProgressMaximum = analysis.Worksheets.Count;
        ProgressValue = 0;
        OperationText = "Preparando generación...";
        var generatedProgress = 0;
        var progress = new Progress<string>(message =>
        {
            OperationText = message;
            ProgressValue = Math.Min(++generatedProgress, ProgressMaximum);
        });
        try
        {
            var request = new PaymentGenerationRequest
            {
                SourceWorkbookPath = GeneralWorkbookPath,
                Period = periodWindow.Period,
                OutputDirectory = periodWindow.OutputDirectory,
                Analysis = analysis,
                Assignments = mapping.Assignments,
                ReplaceExistingFiles = replaceExisting
            };
            var batch = await Task.Run(() =>
                _paymentWorkbookGenerationService.Generate(
                    request,
                    _paymentGenerationHistory,
                    progress));
            _activePaymentGeneration = batch;
            OnPropertyChanged(nameof(HasActivePaymentGeneration));
            GeneratedOutputDirectory = batch.OutputDirectory;
            ApplyGeneratedAttachments(batch);
            var selection = SelectBrokersWithCurrentGeneratedFiles(batch);
            GenerationStatusText = selection.Succeeded
                ? $"{batch.Period}: {batch.Files.Count} archivo(s) listos; " +
                  $"{selection.SelectedBrokerCount} corredor(es) seleccionado(s) automáticamente."
                : $"{batch.Period}: archivos listos, pero la selección automática no pudo completarse.";
            SaveCurrentSession();
            OperationText = selection.Succeeded
                ? $"Generación completada: {batch.Files.Count} archivo(s); " +
                  $"{selection.SelectedBrokerCount} corredor(es) seleccionado(s)."
                : selection.ErrorMessage;

            var perBroker = batch.Files.GroupBy(value => value.BrokerId)
                .Select(group => $"• {group.First().BrokerName}: {group.Count()} archivo(s)");
            var warningText = batch.Warnings.Count == 0
                ? string.Empty
                : "\n\nAdvertencias:\n" + string.Join(Environment.NewLine,
                    batch.Warnings.Distinct(StringComparer.OrdinalIgnoreCase).Select(value => $"• {value}"));
            var excludedText = selection.ExcludedBrokerCount == 0
                ? string.Empty
                : $"\n{selection.ExcludedBrokerCount} corredor(es) no fueron seleccionados porque " +
                  "están inactivos o sus archivos no están disponibles.";
            var selectionErrorText = selection.Succeeded
                ? string.Empty
                : $"\n\n{selection.ErrorMessage}";
            var hasWarnings = batch.Warnings.Count > 0 ||
                              selection.ExcludedBrokerCount > 0 ||
                              !selection.Succeeded;
            MessageBox.Show(
                $"{(hasWarnings ? "Generación completada con advertencias." : "Generación completada.")}\n\n" +
                $"{selection.GeneratedFileCount} archivo(s) generado(s).\n" +
                $"{selection.BrokersWithGeneratedFilesCount} corredor(es) con archivos.\n" +
                $"{selection.SelectedBrokerCount} corredor(es) seleccionado(s) automáticamente." +
                excludedText +
                $"\n\nCarpeta:\n{batch.OutputDirectory}\n\n" +
                string.Join(Environment.NewLine, perBroker) +
                warningText +
                selectionErrorText +
                "\n\nNo se enviaron correos.",
                hasWarnings ? "Generación completada con advertencias" : "Generación completada",
                MessageBoxButton.OK,
                hasWarnings ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ClearBrokerSelection();
            SaveCurrentSessionWithMessageOnError(false);
            _logger.Error("No fue posible generar los detalles de pago.", ex);
            OperationText = "La generación falló; no se dejaron archivos parciales válidos.";
            GenerationStatusText = $"Error de generación: {ex.Message}";
            MessageBox.Show(
                $"No fue posible generar los archivos.\n\n{ex.Message}",
                "Error de generación", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void OpenGeneratedFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!CanOpenGeneratedFolder)
        {
            MessageBox.Show(
                "La carpeta de la generación no está disponible.",
                "Abrir carpeta", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = GeneratedOutputDirectory,
            UseShellExecute = true
        });
    }

    private async Task<WorkbookAnalysisResult?> AnalyzeGeneralWorkbookAsync(bool showResult)
    {
        SetBusy(true);
        OperationText = "Analizando todas las pestañas del Excel general...";
        try
        {
            var analysis = await Task.Run(() => _workbookAnalysisService.Analyze(GeneralWorkbookPath));
            _currentWorkbookAnalysis = analysis;
            var errors = analysis.Errors
                .Concat(analysis.Worksheets.SelectMany(value =>
                    value.Errors.Select(error => $"{value.WorksheetName}: {error}")))
                .ToList();
            if (errors.Count > 0)
            {
                WorkbookAnalysisText =
                    $"{analysis.Worksheets.Count} pestaña(s) detectada(s); {errors.Count} error(es) de interpretación.";
                OperationText = "El Excel general contiene incidencias que bloquean la generación.";
                MessageBox.Show(
                    "No se puede usar el Excel general:\n\n" +
                    string.Join(Environment.NewLine, errors.Select(value => $"• {value}")),
                    "Análisis del Excel", MessageBoxButton.OK, MessageBoxImage.Warning);
                return analysis;
            }

            var currentMapping = _worksheetMappingService.Resolve(
                analysis.Worksheets.Select(value => value.WorksheetName),
                _configuration.Brokers);
            var brokerCount = currentMapping.Assignments
                .Select(value => value.Broker.Id).Distinct().Count();
            WorkbookAnalysisText =
                $"{analysis.Worksheets.Count} pestaña(s), {brokerCount} corredor(es) asociado(s).";
            OperationText = "Excel general analizado.";
            if (showResult)
            {
                var warnings = analysis.Worksheets.SelectMany(value => value.Warnings).ToList();
                MessageBox.Show(
                    $"Excel cargado: {Path.GetFileName(GeneralWorkbookPath)}\n\n" +
                    $"Pestañas: {analysis.Worksheets.Count}\n" +
                    $"Asociadas actualmente: {currentMapping.Assignments.Count}\n" +
                    $"Corredores relacionados: {brokerCount}\n" +
                    $"Sin asociación: {currentMapping.MissingWorksheetNames.Count}" +
                    (warnings.Count == 0
                        ? string.Empty
                        : $"\n\nAdvertencias:\n{string.Join(Environment.NewLine, warnings.Select(value => $"• {value}"))}"),
                    "Análisis del Excel", MessageBoxButton.OK,
                    warnings.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }

            return analysis;
        }
        catch (Exception ex)
        {
            _logger.Error("Error inesperado al analizar el Excel general.", ex);
            WorkbookAnalysisText = "No fue posible analizar el Excel general.";
            OperationText = "Error al analizar el Excel general.";
            MessageBox.Show(
                $"No fue posible analizar el Excel general.\n\n{ex.Message}",
                "Análisis del Excel", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        finally
        {
            SetBusy(false);
        }
    }

    private GeneratedBrokerSelectionResult SelectBrokersWithCurrentGeneratedFiles(
        PaymentGenerationBatch batch)
    {
        _isBulkSelectionUpdate = true;
        try
        {
            return _generatedBrokerSelectionService.SelectEligibleBrokers(
                BrokerItems,
                _configuration.Brokers,
                batch,
                GeneralWorkbookPath);
        }
        finally
        {
            _isBulkSelectionUpdate = false;
            OnPropertyChanged(nameof(AreAllBrokersSelected));
            OnPropertyChanged(nameof(CanSendSelected));
        }
    }

    private void ClearBrokerSelection()
    {
        _isBulkSelectionUpdate = true;
        try
        {
            GeneratedBrokerSelectionService.ClearSelection(BrokerItems);
        }
        finally
        {
            _isBulkSelectionUpdate = false;
            OnPropertyChanged(nameof(AreAllBrokersSelected));
            OnPropertyChanged(nameof(CanSendSelected));
        }
    }

    private void ApplyGeneratedAttachments(PaymentGenerationBatch batch)
    {
        foreach (var item in BrokerItems)
        {
            foreach (var previousGenerated in item.GeneratedAttachmentPaths.ToList())
            {
                item.AttachmentPaths.Remove(previousGenerated);
            }

            item.GeneratedAttachmentPaths.Clear();
        }

        foreach (var group in batch.Files.GroupBy(value => value.BrokerId))
        {
            var item = BrokerItems.FirstOrDefault(value => value.BrokerId == group.Key);
            if (item is null)
            {
                continue;
            }

            foreach (var file in group)
            {
                if (!item.AttachmentPaths.Contains(file.OutputPath, StringComparer.OrdinalIgnoreCase))
                {
                    item.AttachmentPaths.Add(file.OutputPath);
                }

                item.GeneratedAttachmentPaths.Add(file.OutputPath);
            }

            item.LastError = string.Empty;
            UpdateReadiness(item);
        }
    }

    private void AttachFiles_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not BrokerSendItem item)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Adjuntar archivos para {item.BrokerName}",
            Filter = "Archivos de Excel (*.xlsx;*.xls)|*.xlsx;*.xls",
            Multiselect = true,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var existing = new HashSet<string>(item.AttachmentPaths.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var selectedPath in dialog.FileNames)
        {
            var fullPath = Path.GetFullPath(selectedPath);
            if (EmailValidationService.IsAllowedExcelFile(fullPath) && existing.Add(fullPath))
            {
                item.AttachmentPaths.Add(fullPath);
                added++;
            }
        }

        item.LastError = string.Empty;
        UpdateReadiness(item);
        SaveCurrentSessionWithMessageOnError();
        OperationText = added == 0
            ? "Los archivos seleccionados ya estaban adjuntos."
            : $"Se agregaron {added} archivo(s) a {item.BrokerName}.";
    }

    private void ViewGeneratedFiles_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not BrokerSendItem item)
        {
            return;
        }

        if (_activePaymentGeneration is null)
        {
            MessageBox.Show(
                "Todavía no se han generado archivos para esta sesión.",
                "Archivos generados", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var files = _generatedFileViewerService.GetFilesForBroker(_activePaymentGeneration, item);
        if (files.Count == 0)
        {
            MessageBox.Show(
                $"No hay archivos generados asociados a {item.BrokerName}.",
                "Archivos generados", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        new GeneratedFilesWindow(item, files, _generatedFileViewerService)
        {
            Owner = this
        }.ShowDialog();
    }

    private void RemoveAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not BrokerSendItem item || button.DataContext is not string path)
        {
            return;
        }

        item.AttachmentPaths.Remove(path);
        item.GeneratedAttachmentPaths.Remove(path);
        item.LastError = string.Empty;
        UpdateReadiness(item);
        SaveCurrentSessionWithMessageOnError();
    }

    private async void SendSelected_Click(object sender, RoutedEventArgs e)
    {
        await SendSelectedBrokersAsync("Enviar seleccionados");
    }

    private async void SendAll_Click(object sender, RoutedEventArgs e)
    {
        await SendSelectedBrokersAsync("Enviar todos");
    }

    private async Task SendSelectedBrokersAsync(string dialogTitle)
    {
        var selectedItems = EmailBatchSelection.GetSelected(BrokerItems);
        if (selectedItems.Count == 0)
        {
            MessageBox.Show("Seleccione al menos un corredor para enviar.",
                dialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var reviewItems = selectedItems.Where(value => value.RequiresReview).ToList();
        var confirmedReviewIds = new HashSet<Guid>();
        if (reviewItems.Count > 0)
        {
            var reviewSummary = string.Join(Environment.NewLine,
                reviewItems.Select(value => $"• {value.BrokerIdentityText}: {value.ReviewNote}"));
            if (MessageBox.Show(
                    "Los siguientes corredores requieren revisión antes del envío:\n\n" + reviewSummary +
                    "\n\n¿Confirma explícitamente que desea validarlos para este lote?",
                    "Revisión requerida", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            foreach (var reviewItem in reviewItems)
            {
                confirmedReviewIds.Add(reviewItem.BrokerId);
            }
        }

        var validRequests = new List<EmailSendRequest>();
        var requestItems = new Dictionary<Guid, BrokerSendItem>();
        var invalidCount = 0;
        foreach (var item in selectedItems)
        {
            var request = BuildRequest(item, out var errors, confirmedReviewIds.Contains(item.BrokerId));
            if (errors.Count > 0)
            {
                invalidCount++;
                MarkInvalid(item, errors);
                continue;
            }

            validRequests.Add(request);
            requestItems[request.RequestId] = item;
        }

        var summary = $"Está por enviar {validRequests.Count} correo(s).\n\n" +
                      $"Corredores seleccionados: {selectedItems.Count}\n" +
                      $"Correos listos: {validRequests.Count}\n" +
                      $"Correos con errores: {invalidCount}";
        if (validRequests.Count == 0)
        {
            MessageBox.Show(summary + "\n\nCorrija los errores mostrados en la tabla antes de enviar.",
                "Resumen del envío", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (invalidCount > 0)
        {
            summary += "\n\nLos corredores con errores no se enviarán. ¿Desea continuar con los correos listos?";
        }
        else
        {
            summary += "\n\n¿Desea continuar?";
        }

        if (MessageBox.Show(summary, "Confirmar envío múltiple", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        await SendRequestsAsync(validRequests, requestItems);
    }

    private void NewBatch_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("¿Desea iniciar un nuevo envío?\n\nSe quitarán todos los archivos adjuntos y se restablecerán los estados.",
                "Nuevo envío", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var resetChoice = MessageBox.Show(
            "¿Desea restablecer también el asunto y el mensaje a los valores guardados?\n\nSí: restablecer.\nNo: conservar el texto actual.",
            "Asunto y mensaje", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (resetChoice == MessageBoxResult.Cancel)
        {
            return;
        }

        foreach (var item in BrokerItems)
        {
            item.AttachmentPaths.Clear();
            item.GeneratedAttachmentPaths.Clear();
            item.Status = item.RequiresReview ? SendStatus.ReviewRequired : SendStatus.Pending;
            if (item.RequiresReview)
            {
                item.IsSelected = false;
            }
            item.LastError = string.Empty;
            item.IsSending = false;
        }

        _activePaymentGeneration = null;
        OnPropertyChanged(nameof(HasActivePaymentGeneration));
        GeneratedOutputDirectory = string.Empty;
        GenerationStatusText = "Todavía no se han generado archivos para esta sesión.";

        if (resetChoice == MessageBoxResult.Yes)
        {
            Subject = _configuration.DefaultSubject;
            Message = _configuration.DefaultMessage;
        }

        SaveCurrentSessionWithMessageOnError();
        OperationText = "Nuevo envío preparado.";
        ProgressValue = 0;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveConfigurationAndSession())
        {
            return;
        }

        OperationText = "Configuración y sesión guardadas.";
        MessageBox.Show("Los cambios se guardaron correctamente.",
            "Guardar", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void EmailField_LostFocus(object sender, RoutedEventArgs e) => SaveCurrentSessionWithMessageOnError(false);

    private void RecentSends_Click(object sender, RoutedEventArgs e)
    {
        if (_recentRecords.Count == 0)
        {
            MessageBox.Show("Todavía no existen registros de envíos para mostrar.",
                "Envíos recientes", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenResendWindow(null);
    }

    private void ResendRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not BrokerSendItem item)
        {
            return;
        }

        var record = _recentRecords
            .Where(value => value.BrokerId == item.BrokerId && value.WasSuccessful)
            .OrderByDescending(value => value.SentAt)
            .FirstOrDefault();
        if (record is null)
        {
            MessageBox.Show("Este corredor aún no tiene un correo enviado que pueda reenviarse.",
                "Reenviar", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenResendWindow(record);
    }

    private void OpenResendWindow(SentEmailRecord? initialRecord)
    {
        var window = new ResendWindow(
            _recentRecords,
            initialRecord,
            CommonCcText,
            _configuration,
            _validationService,
            _outlookService,
            _archiveService,
            _sessionService,
            _logger)
        {
            Owner = this
        };
        window.ShowDialog();
        if (window.NewRecord is { } newRecord)
        {
            var item = BrokerItems.FirstOrDefault(value => value.BrokerId == newRecord.BrokerId);
            if (item is not null)
            {
                item.Status = newRecord.WasSuccessful ? SendStatus.Sent : SendStatus.Error;
                item.LastError = newRecord.ErrorMessage;
            }

            SaveCurrentSessionWithMessageOnError(false);
        }
    }

    private async Task SendRequestsAsync(
        IReadOnlyList<EmailSendRequest> requests,
        IReadOnlyDictionary<Guid, BrokerSendItem> requestItems)
    {
        if (_isBusy || requests.Any(request => requestItems[request.RequestId].IsSending))
        {
            return;
        }

        SetBusy(true);
        ProgressMaximum = requests.Count;
        ProgressValue = 0;
        var progress = new Progress<OutlookSendProgress>(update =>
        {
            if (!requestItems.TryGetValue(update.Request.RequestId, out var item))
            {
                return;
            }

            if (update.Stage == OutlookProgressStage.Starting)
            {
                item.IsSending = true;
                item.Status = SendStatus.Sending;
                item.LastError = string.Empty;
                OperationText = $"Enviando correo {update.Current} de {update.Total}: {item.BrokerName}...";
            }
            else if (update.Result is not null)
            {
                item.IsSending = false;
                item.Status = update.Result.WasSuccessful ? SendStatus.Sent : SendStatus.Error;
                item.LastError = update.Result.ErrorMessage;
                ProgressValue = update.Current;
            }
        });

        try
        {
            var results = await _outlookService.SendBatchAsync(requests, progress);
            var successCount = 0;
            foreach (var result in results)
            {
                var request = requests.First(value => value.RequestId == result.RequestId);
                var item = requestItems[result.RequestId];
                var record = CreateSentRecord(request, result, item);
                _recentRecords.Insert(0, record);
                if (result.WasSuccessful)
                {
                    successCount++;
                }
            }

            _sessionService.SaveRecentSends(_recentRecords);
            UpdateActiveGenerationAfterSend(requests, results);
            SaveCurrentSession();
            var failedCount = results.Count - successCount;
            OperationText = $"Proceso finalizado. Enviados: {successCount}. Con error: {failedCount}.";
            MessageBox.Show(OperationText,
                "Resultado del envío",
                MessageBoxButton.OK,
                failedCount == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            _logger.Error("Error inesperado durante el proceso de envío.", ex);
            OperationText = "El proceso terminó con un error inesperado.";
            MessageBox.Show($"No fue posible completar el proceso.\n\n{ex.Message}",
                "Error de envío", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            foreach (var item in requestItems.Values)
            {
                item.IsSending = false;
                if (item.Status == SendStatus.Sending)
                {
                    item.Status = SendStatus.Error;
                    item.LastError = "El envío no pudo finalizar.";
                }
            }

            SetBusy(false);
        }
    }

    private SentEmailRecord CreateSentRecord(EmailSendRequest request, EmailSendResult result, BrokerSendItem item)
    {
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
                item.LastError = recordError;
            }
        }

        return new SentEmailRecord
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
            ResendOfRecordId = request.ResendOfRecordId,
            PaymentGenerationId = request.PaymentGenerationId
        };
    }

    private void UpdateActiveGenerationAfterSend(
        IReadOnlyList<EmailSendRequest> requests,
        IReadOnlyList<EmailSendResult> results)
    {
        if (_activePaymentGeneration is null ||
            requests.All(value => value.PaymentGenerationId != _activePaymentGeneration.Id))
        {
            return;
        }

        foreach (var result in results)
        {
            var request = requests.First(value => value.RequestId == result.RequestId);
            if (request.PaymentGenerationId != _activePaymentGeneration.Id)
            {
                continue;
            }

            if (result.WasSuccessful)
            {
                if (!_activePaymentGeneration.SentBrokerIds.Contains(request.BrokerId))
                {
                    _activePaymentGeneration.SentBrokerIds.Add(request.BrokerId);
                }

                _activePaymentGeneration.FailedBrokerIds.Remove(request.BrokerId);
            }
            else if (!_activePaymentGeneration.SentBrokerIds.Contains(request.BrokerId) &&
                     !_activePaymentGeneration.FailedBrokerIds.Contains(request.BrokerId))
            {
                _activePaymentGeneration.FailedBrokerIds.Add(request.BrokerId);
            }
        }

        var requiredBrokerIds = _activePaymentGeneration.Files
            .Select(value => value.BrokerId)
            .Distinct()
            .ToHashSet();
        _activePaymentGeneration.Status = requiredBrokerIds.All(_activePaymentGeneration.SentBrokerIds.Contains)
            ? PaymentGenerationStatus.Sent
            : _activePaymentGeneration.SentBrokerIds.Count > 0
                ? PaymentGenerationStatus.PartialSend
                : _activePaymentGeneration.FailedBrokerIds.Count > 0
                    ? PaymentGenerationStatus.Failed
                    : PaymentGenerationStatus.ReadyToSend;
        _generationHistoryService.Upsert(_paymentGenerationHistory, _activePaymentGeneration);
        GenerationStatusText = _activePaymentGeneration.Status switch
        {
            PaymentGenerationStatus.Sent => $"{_activePaymentGeneration.Period}: envío completo.",
            PaymentGenerationStatus.PartialSend => $"{_activePaymentGeneration.Period}: envío parcial.",
            PaymentGenerationStatus.Failed => $"{_activePaymentGeneration.Period}: envío fallido.",
            _ => $"{_activePaymentGeneration.Period}: archivos listos para envío."
        };
    }

    private EmailSendRequest BuildRequest(BrokerSendItem item, out List<string> errors, bool reviewConfirmed = false)
    {
        errors = [];
        var broker = _configuration.Brokers.FirstOrDefault(value => value.Id == item.BrokerId);
        if (broker is null)
        {
            errors.Add("No se encontró la configuración del corredor.");
            return new EmailSendRequest { BrokerId = item.BrokerId, BrokerName = item.BrokerName };
        }

        if (!broker.IsActive)
        {
            errors.Add("El corredor está inactivo y no puede recibir envíos.");
        }

        var recipients = _validationService.ResolveRecipients(broker, CommonCcText);
        errors.AddRange(recipients.Errors);

        var request = new EmailSendRequest
        {
            BrokerId = item.BrokerId,
            BrokerName = item.BrokerName,
            BrokerPrimaryRecipients = recipients.BrokerPrimaryRecipients,
            AssistantRecipients = recipients.AssistantRecipients,
            ToRecipients = recipients.ToRecipients,
            CcRecipients = recipients.CcRecipients,
            Subject = Subject.Trim(),
            Body = Message,
            AttachmentPaths = item.AttachmentPaths.ToList(),
            SignatureImagePath = _configuration.SignatureImagePath,
            RequiresReview = item.RequiresReview,
            ReviewNote = item.ReviewNote,
            ReviewConfirmed = reviewConfirmed,
            PaymentGenerationId = item.GeneratedAttachmentPaths.Count > 0
                ? _activePaymentGeneration?.Id
                : null
        };
        errors.AddRange(_generationHistoryService.ValidateGeneratedAttachments(
            _activePaymentGeneration,
            item.BrokerId,
            item.GeneratedAttachmentPaths));
        errors.AddRange(_validationService.ValidateRequest(request));
        errors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return request;
    }

    private bool? ConfirmReviewIfRequired(BrokerSendItem item)
    {
        if (!item.RequiresReview)
        {
            return false;
        }

        var message = $"'{item.BrokerIdentityText}' requiere revisión.\n\n{item.ReviewNote}\n\n" +
                      "¿Confirma explícitamente que desea continuar con este registro?";
        return MessageBox.Show(message, "Revisión requerida", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes
            ? true
            : null;
    }

    private void MarkInvalid(BrokerSendItem item, IEnumerable<string> errors)
    {
        item.Status = SendStatus.Error;
        item.LastError = string.Join(" ", errors);
    }

    private void ShowValidationErrors(string brokerName, IReadOnlyCollection<string> errors) =>
        MessageBox.Show($"No se puede enviar el correo de '{brokerName}':\n\n" +
                        string.Join(Environment.NewLine, errors.Select(error => $"• {error}")),
            "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);

    private BrokerSendItem? GetSelectedItemOrWarn()
    {
        var item = SelectedBrokerItem ?? BrokersGrid.SelectedItem as BrokerSendItem;
        if (item is null)
        {
            MessageBox.Show("Seleccione un corredor en la tabla.",
                "Corredor", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return item;
    }

    private void PersistBrokerChanges()
    {
        try
        {
            _configurationService.Save(_configuration);
            ReloadBrokerRows(BrokerItems.ToList());
            SaveCurrentSession();
            OperationText = "Datos de corredores guardados.";
        }
        catch (Exception ex)
        {
            ShowSaveError(ex);
        }
    }

    private void ReloadBrokerRows(IEnumerable<BrokerSendItem>? preservedItems)
    {
        var preserved = (preservedItems ?? []).ToDictionary(item => item.BrokerId);
        var selectedBrokerId = SelectedBrokerItem?.BrokerId;
        BrokerItems.Clear();
        InactiveBrokers.Clear();
        foreach (var inactiveBroker in _configuration.Brokers
                     .Where(value => !value.IsActive)
                     .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(value => value.PrimaryEmailAddresses.FirstOrDefault(), StringComparer.OrdinalIgnoreCase))
        {
            InactiveBrokers.Add(inactiveBroker);
        }

        foreach (var broker in _configuration.Brokers.Where(value => value.IsActive)
                     .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
                     .ThenBy(value => value.PrimaryEmailAddresses.FirstOrDefault(), StringComparer.OrdinalIgnoreCase))
        {
            var isNewItem = false;
            if (!preserved.TryGetValue(broker.Id, out var item))
            {
                isNewItem = true;
                item = new BrokerSendItem
                {
                    BrokerId = broker.Id,
                    IsSelected = !broker.RequiresReview,
                    Status = broker.RequiresReview ? SendStatus.ReviewRequired : SendStatus.Pending
                };
            }

            var previouslyRequiredReview = item.RequiresReview;
            var legacyBatchReview = !item.RequiresBatchReview &&
                                    item.RequiresReview &&
                                    !broker.RequiresReview &&
                                    item.AttachmentPaths.Count > 0 &&
                                    item.ReviewNote.Contains("archivos adjuntos", StringComparison.OrdinalIgnoreCase);
            if (legacyBatchReview)
            {
                item.RequiresBatchReview = true;
                item.BatchReviewNote = item.ReviewNote;
            }

            var batchRequiresReview = item.RequiresBatchReview;
            var batchReviewNote = batchRequiresReview ? item.BatchReviewNote : string.Empty;
            item.BrokerName = broker.Name;
            item.SeedKey = broker.SeedKey;
            item.PrimaryRecipients = [.. broker.PrimaryEmailAddresses];
            item.Assistants = broker.Assistants.Select(value => value.Clone()).ToList();
            item.RequiresReview = broker.RequiresReview || batchRequiresReview;
            item.ReviewNote = batchRequiresReview ? batchReviewNote : broker.ReviewNote ?? string.Empty;
            item.IsSending = false;
            if (item.Status == SendStatus.Sending)
            {
                item.Status = SendStatus.Pending;
            }

            if (item.RequiresReview && (isNewItem || item.Status is SendStatus.Pending or SendStatus.Ready))
            {
                item.Status = SendStatus.ReviewRequired;
                item.IsSelected = false;
            }
            else if (!item.RequiresReview && previouslyRequiredReview && item.Status == SendStatus.ReviewRequired)
            {
                item.LastError = string.Empty;
                UpdateReadiness(item);
            }

            item.RefreshComputedProperties();
            item.PropertyChanged -= BrokerItem_PropertyChanged;
            item.PropertyChanged += BrokerItem_PropertyChanged;
            BrokerItems.Add(item);
        }

        SelectedBrokerItem = BrokerItems.FirstOrDefault(item => item.BrokerId == selectedBrokerId) ?? BrokerItems.FirstOrDefault();
        _brokerItemsView.Refresh();
        OnPropertyChanged(nameof(AreAllBrokersSelected));
        OnPropertyChanged(nameof(CanSendSelected));
        OnPropertyChanged(nameof(InactiveBrokersButtonText));
        OnPropertyChanged(nameof(BrokerSummaryText));
    }

    private void BrokerItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(BrokerSendItem.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(AreAllBrokersSelected));
        OnPropertyChanged(nameof(CanSendSelected));
        if (_loaded && !_isBusy && !_isBulkSelectionUpdate)
        {
            SaveCurrentSessionWithMessageOnError(false);
        }
    }

    private void ToggleAllBrokers_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || BrokerItems.Count == 0)
        {
            OnPropertyChanged(nameof(AreAllBrokersSelected));
            return;
        }

        var selectAll = BrokerItems.Any(item => !item.IsSelected);
        _isBulkSelectionUpdate = true;
        try
        {
            foreach (var item in BrokerItems)
            {
                item.IsSelected = selectAll;
            }
        }
        finally
        {
            _isBulkSelectionUpdate = false;
        }

        OnPropertyChanged(nameof(AreAllBrokersSelected));
        OnPropertyChanged(nameof(CanSendSelected));
        if (_loaded)
        {
            SaveCurrentSessionWithMessageOnError(false);
        }
    }

    private void UpdateReadiness(BrokerSendItem item)
    {
        if (item.RequiresReview)
        {
            item.Status = SendStatus.ReviewRequired;
            return;
        }

        var request = BuildRequest(item, out var errors);
        item.Status = errors.Count == 0 && _validationService.ValidateRequest(request).Count == 0
            ? SendStatus.Ready
            : SendStatus.Pending;
    }

    private bool SaveConfigurationAndSession()
    {
        if (!_validationService.TryParseAddresses(CommonCcText, false, out var ccAddresses, out var errors))
        {
            MessageBox.Show("Las copias generales contienen errores:\n\n" +
                            string.Join(Environment.NewLine, errors.Select(error => $"• {error}")),
                "Copias generales", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        try
        {
            _configuration.DefaultSubject = Subject;
            _configuration.DefaultMessage = Message;
            _configuration.CommonCcAddresses = ccAddresses;
            CommonCcText = string.Join(Environment.NewLine, ccAddresses);
            _configurationService.Save(_configuration);
            SaveCurrentSession();
            return true;
        }
        catch (Exception ex)
        {
            ShowSaveError(ex);
            return false;
        }
    }

    private void SaveCurrentSession() => _sessionService.SaveCurrent(new CurrentSession
    {
        Subject = Subject,
        Message = Message,
        CommonCcText = CommonCcText,
        GeneralWorkbookPath = GeneralWorkbookPath,
        ActivePaymentGenerationId = _activePaymentGeneration?.Id,
        GeneratedOutputDirectory = GeneratedOutputDirectory,
        GeneratedPeriod = _activePaymentGeneration?.Period ?? string.Empty,
        BrokerItems = BrokerItems.ToList()
    });

    private void SaveCurrentSessionWithMessageOnError(bool showMessage = true)
    {
        try
        {
            SaveCurrentSession();
        }
        catch (Exception ex)
        {
            _logger.Error("No fue posible guardar automáticamente la sesión.", ex);
            if (showMessage)
            {
                ShowSaveError(ex);
            }
        }
    }

    private void ShowSaveError(Exception ex) => MessageBox.Show(
        $"No fue posible guardar los cambios.\n\n{ex.Message}",
        "Error al guardar", MessageBoxButton.OK, MessageBoxImage.Error);

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        OnPropertyChanged(nameof(IsUiEnabled));
        OnPropertyChanged(nameof(CanSendSelected));
    }

    private bool FilterBrokerItem(object value)
    {
        if (value is not BrokerSendItem item || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var query = SearchText.Trim();
        return item.BrokerName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               item.PrimaryRecipients.Any(address => address.Contains(query, StringComparison.OrdinalIgnoreCase)) ||
               item.Assistants.Any(assistant =>
                   assistant.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                   assistant.Email.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void LoadSignaturePreview()
    {
        _signatureLoadWarning = null;
        if (string.IsNullOrWhiteSpace(_configuration.SignatureImagePath))
        {
            ClearSignaturePreview("No hay una firma configurada.");
            return;
        }

        try
        {
            var info = SignatureImageService.ValidateFile(_configuration.SignatureImagePath);
            SignaturePreview = _signatureService.LoadPreview(info.Path);
            SignatureFileName = info.FileName;
            SignatureStateText = $"{info.PixelWidth} × {info.PixelHeight} px";
            NotifySignatureProperties();
        }
        catch (SignatureImageException ex)
        {
            ClearSignaturePreview("La firma configurada no está disponible.");
            _signatureLoadWarning = ex.Message + " Seleccione nuevamente la imagen o quite la firma configurada.";
            _logger.Error("La firma configurada no pudo cargarse.", ex);
        }
    }

    private void ClearSignaturePreview(string stateText)
    {
        SignaturePreview = null;
        SignatureFileName = string.IsNullOrWhiteSpace(_configuration.SignatureImagePath)
            ? string.Empty
            : Path.GetFileName(_configuration.SignatureImagePath);
        SignatureStateText = stateText;
        NotifySignatureProperties();
    }

    private void NotifySignatureProperties()
    {
        OnPropertyChanged(nameof(HasSignature));
        OnPropertyChanged(nameof(HasConfiguredSignature));
        OnPropertyChanged(nameof(SignaturePreviewVisibility));
        OnPropertyChanged(nameof(SignatureEmptyVisibility));
        OnPropertyChanged(nameof(SignatureSelectButtonText));
    }

    private void RenderUiSnapshot(string path)
    {
        UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        encoder.Save(stream);
    }

    private void TryDeleteManagedSignature(string? path)
    {
        try
        {
            _signatureService.DeleteIfManaged(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"La configuración se guardó, pero no fue posible eliminar la copia anterior de la firma: {path}", ex);
        }
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
