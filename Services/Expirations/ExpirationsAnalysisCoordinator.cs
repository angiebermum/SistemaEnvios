using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsAnalysisCoordinator
{
    ExpirationsAnalysisSessionSnapshot Snapshot { get; }
    void SelectProcess(ExpirationsProcess? process);
    void SelectFile(string sourcePath);
    Task<ExpirationsAnalysisSessionSnapshot> AnalyzeAsync(
        ExpirationsWorkbookReadOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<ExpirationsAnalysisSessionSnapshot> RefreshCatalogAndReanalyzeAsync(
        CancellationToken cancellationToken = default);
    Task<ExpirationsWorkbookInspection> InspectWorkbookAsync(CancellationToken cancellationToken = default);
    ExpirationsManualOverrideResult ApplyManualOverride(
        uint rowNumber,
        int componentIndex,
        Guid brokerId);
    Task<ExpirationsAssociationConfirmationResult> ConfirmAssociationAsync(
        ExpirationsAssociationConfirmation confirmation,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsAnalysisCoordinator : IExpirationsAnalysisCoordinator
{
    private readonly ExpirationsBrokerCatalogService _catalogService;
    private readonly IExpirationsBrokerAssociationRepository _associations;
    private readonly IExpirationsWorkbookReader _reader;
    private readonly ExpirationsWorkbookAnalysisService _analysisService;
    private readonly ExpirationsDistributionPreviewService _previewService;
    private readonly IExpirationsWorkbookInspectionService _inspectionService;
    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;
    private ExpirationsProcess? _process;
    private string _sourcePath = string.Empty;
    private ExpirationsWorkbookReadOptions? _readOptions;
    private ExpirationsWorkbookReadResult? _readResult;
    private ExpirationsBrokerCatalog? _catalog;
    private IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>> _associationDocuments = [];
    private readonly List<ExpirationsManualResolutionOverride> _manualOverrides = [];

    public ExpirationsAnalysisCoordinator(
        ExpirationsBrokerCatalogService catalogService,
        IExpirationsBrokerAssociationRepository associations,
        IExpirationsWorkbookReader? reader = null,
        ExpirationsWorkbookAnalysisService? analysisService = null,
        ExpirationsDistributionPreviewService? previewService = null,
        IExpirationsWorkbookInspectionService? inspectionService = null,
        ExpirationsBrokerNormalizer? normalizer = null,
        TimeProvider? timeProvider = null)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _associations = associations ?? throw new ArgumentNullException(nameof(associations));
        _reader = reader ?? new ExpirationsWorkbookReader();
        _analysisService = analysisService ?? new ExpirationsWorkbookAnalysisService();
        _previewService = previewService ?? new ExpirationsDistributionPreviewService();
        _inspectionService = inspectionService ?? new ExpirationsWorkbookInspectionService();
        _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
        Snapshot = EmptySnapshot();
    }

    public ExpirationsAnalysisSessionSnapshot Snapshot { get; private set; }

    public void SelectProcess(ExpirationsProcess? process)
    {
        if (_process == process)
            return;
        _process = process;
        ClearAnalysis(preserveSourcePath: true);
    }

    public void SelectFile(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        _sourcePath = Path.GetFullPath(sourcePath);
        ClearAnalysis(preserveSourcePath: true);
    }

    public async Task<ExpirationsAnalysisSessionSnapshot> AnalyzeAsync(
        ExpirationsWorkbookReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (_process is null)
            throw new InvalidOperationException("Seleccione el proceso de Vencimientos antes de analizar.");
        if (string.IsNullOrWhiteSpace(_sourcePath))
            throw new InvalidOperationException("Seleccione el archivo .xlsx antes de analizar.");

        _readOptions = options;
        _manualOverrides.Clear();
        var readResult = await Task.Run(
            () => _reader.Read(_sourcePath, options),
            cancellationToken);
        _readResult = readResult;
        if (!readResult.IsSuccess)
        {
            _catalog = null;
            _associationDocuments = [];
            Snapshot = EmptySnapshot(readResult, readResult.Messages);
            return Snapshot;
        }

        var catalogTask = _catalogService.LoadAsync(cancellationToken);
        var associationsTask = _associations.ListAsync(cancellationToken);
        await Task.WhenAll(catalogTask, associationsTask);
        _catalog = await catalogTask;
        _associationDocuments = await associationsTask;
        Snapshot = BuildSnapshot();
        return Snapshot;
    }

    public async Task<ExpirationsAnalysisSessionSnapshot> RefreshCatalogAndReanalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        var catalogTask = _catalogService.LoadAsync(cancellationToken);
        var associationsTask = _associations.ListAsync(cancellationToken);
        await Task.WhenAll(catalogTask, associationsTask);
        _catalog = await catalogTask;
        _associationDocuments = await associationsTask;
        _manualOverrides.RemoveAll(value => !IsActiveCatalogBroker(value.BrokerId));
        Snapshot = _readResult?.IsSuccess == true
            ? BuildSnapshot()
            : EmptySnapshot(_readResult, _readResult?.Messages ?? []);
        return Snapshot;
    }

    public Task<ExpirationsWorkbookInspection> InspectWorkbookAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_sourcePath))
            throw new InvalidOperationException("Seleccione el archivo .xlsx antes de inspeccionarlo.");
        return Task.Run(() => _inspectionService.Inspect(_sourcePath), cancellationToken);
    }

    public ExpirationsManualOverrideResult ApplyManualOverride(
        uint rowNumber,
        int componentIndex,
        Guid brokerId)
    {
        if (_readResult is null || _catalog is null || Snapshot.Analysis is null)
            return OverrideRejected("No existe un análisis activo para aplicar la selección.");
        if (!IsActiveCatalogBroker(brokerId))
            return OverrideRejected("El corredor seleccionado no existe o está inactivo para Vencimientos.");
        var component = FindComponent(rowNumber, componentIndex);
        if (component is null || component.Status is not (
                ExpirationsBrokerResolutionStatus.Ambiguous or
                ExpirationsBrokerResolutionStatus.Unresolved))
        {
            return OverrideRejected("El elemento seleccionado no admite una resolución manual de sesión.");
        }

        _manualOverrides.RemoveAll(value =>
            value.RowNumber == rowNumber && value.ComponentIndex == componentIndex);
        _manualOverrides.Add(new ExpirationsManualResolutionOverride(rowNumber, componentIndex, brokerId));
        Snapshot = BuildSnapshot();
        return new ExpirationsManualOverrideResult
        {
            Applied = true,
            Message = "La selección se aplicó únicamente a esta fila del archivo actual.",
            Snapshot = Snapshot
        };
    }

    public async Task<ExpirationsAssociationConfirmationResult> ConfirmAssociationAsync(
        ExpirationsAssociationConfirmation confirmation,
        CancellationToken cancellationToken = default)
    {
        if (_readResult is null || _catalog is null || Snapshot.Analysis is null)
            return Confirmation(ExpirationsAssociationConfirmationOutcome.Rejected,
                "No existe un análisis activo para confirmar la asociación.");
        var component = FindComponent(confirmation.RowNumber, confirmation.ComponentIndex);
        if (component is null || component.Status != ExpirationsBrokerResolutionStatus.Unresolved ||
            string.IsNullOrWhiteSpace(component.RawValue))
        {
            return Confirmation(ExpirationsAssociationConfirmationOutcome.Rejected,
                "Solo los valores no reconocidos y no vacíos pueden guardarse como asociación.");
        }
        if (!IsActiveCatalogBroker(confirmation.BrokerId))
        {
            return Confirmation(ExpirationsAssociationConfirmationOutcome.Rejected,
                "El corredor seleccionado no existe o está inactivo para Vencimientos.");
        }

        var normalizedValue = _normalizer.Normalize(component.RawValue);
        if (normalizedValue.Length == 0)
        {
            return Confirmation(ExpirationsAssociationConfirmationOutcome.Rejected,
                "No se puede crear una asociación para un valor vacío.");
        }

        try
        {
            var latest = await _associations.ListAsync(cancellationToken);
            var exact = latest
                .Where(document => _normalizer.Normalize(
                    document.Value.NormalizedValue.Length > 0
                        ? document.Value.NormalizedValue
                        : document.Value.Value) == normalizedValue)
                .OrderBy(document => document.Value.Id)
                .ToList();
            if (exact.Any(document =>
                    document.Value.IsActive && document.Value.BrokerId != confirmation.BrokerId))
            {
                _associationDocuments = latest;
                var manual = ApplyManualOverride(
                    confirmation.RowNumber,
                    confirmation.ComponentIndex,
                    confirmation.BrokerId);
                return Confirmation(
                    ExpirationsAssociationConfirmationOutcome.SessionOverride,
                    manual.Applied
                        ? "El valor ya está asociado activamente a otro corredor. La selección se aplicó solo a esta fila y no se guardó."
                        : manual.Message);
            }

            if (exact.FirstOrDefault(document =>
                    document.Value.IsActive && document.Value.BrokerId == confirmation.BrokerId) is not null)
            {
                _associationDocuments = latest;
                Snapshot = BuildSnapshot();
                return Confirmation(
                    ExpirationsAssociationConfirmationOutcome.ExistingActive,
                    "La asociación activa ya existía; no se creó un duplicado.");
            }

            var inactive = exact.FirstOrDefault(document =>
                !document.Value.IsActive && document.Value.BrokerId == confirmation.BrokerId);
            if (inactive is not null)
            {
                var reactivated = CopyAssociation(inactive.Value);
                reactivated.Kind = confirmation.Kind;
                reactivated.Value = component.RawValue;
                reactivated.NormalizedValue = normalizedValue;
                reactivated.IsActive = true;
                reactivated.UpdatedAtUtc = _timeProvider.GetUtcNow();
                await _associations.UpdateAsync(
                    reactivated,
                    inactive.UpdateTime,
                    cancellationToken);
                _associationDocuments = await _associations.ListAsync(cancellationToken);
                Snapshot = BuildSnapshot();
                return Confirmation(
                    ExpirationsAssociationConfirmationOutcome.Reactivated,
                    "La asociación existente fue reactivada y el archivo se analizó nuevamente.");
            }

            var now = _timeProvider.GetUtcNow();
            await _associations.CreateAsync(new ExpirationsBrokerAssociation
            {
                Id = Guid.NewGuid(),
                BrokerId = confirmation.BrokerId,
                Kind = confirmation.Kind,
                Value = component.RawValue,
                NormalizedValue = normalizedValue,
                IsActive = true,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            }, cancellationToken);
            _associationDocuments = await _associations.ListAsync(cancellationToken);
            Snapshot = BuildSnapshot();
            return Confirmation(
                ExpirationsAssociationConfirmationOutcome.Created,
                "La asociación fue guardada y el archivo se analizó nuevamente.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (FirestoreConcurrencyException)
        {
            try
            {
                _associationDocuments = await _associations.ListAsync(cancellationToken);
                Snapshot = BuildSnapshot();
            }
            catch
            {
                // Se conserva el último análisis seguro si la recarga también falla.
            }
            return Confirmation(
                ExpirationsAssociationConfirmationOutcome.ConcurrencyConflict,
                "La información cambió mientras se guardaba. Se recargaron las asociaciones; revise el pendiente nuevamente.");
        }
        catch (Exception ex)
        {
            return Confirmation(
                ExpirationsAssociationConfirmationOutcome.Failed,
                $"No fue posible guardar la asociación: {ex.Message}");
        }
    }

    private ExpirationsAnalysisSessionSnapshot BuildSnapshot()
    {
        if (_readResult is null || _catalog is null)
            return EmptySnapshot(_readResult, _readResult?.Messages ?? []);
        var catalog = _catalog.Items.ToList();
        var analysis = _analysisService.Analyze(
            _readResult,
            catalog,
            _associationDocuments.Select(document => document.Value),
            _manualOverrides);
        var names = catalog.GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var pending = analysis.RowResolutions
            .SelectMany(row => row.Components.Select((component, index) => new
            {
                row.RowNumber,
                ComponentIndex = index,
                Component = component
            }))
            .Where(item => item.Component.Status != ExpirationsBrokerResolutionStatus.Resolved)
            .Select(item => new ExpirationsPendingIssue
            {
                RowNumber = item.RowNumber,
                ComponentIndex = item.ComponentIndex,
                RawValue = item.Component.RawValue,
                NormalizedValue = item.Component.NormalizedValue,
                Status = item.Component.Status,
                StatusText = StatusText(item.Component.Status),
                CandidateBrokerIds = item.Component.CandidateBrokerIds,
                CandidateBrokerNames = string.Join(", ", item.Component.CandidateBrokerIds
                    .Select(id => names.GetValueOrDefault(id) ?? id.ToString("D"))
                    .Order(StringComparer.CurrentCultureIgnoreCase))
            })
            .OrderBy(issue => issue.RowNumber)
            .ThenBy(issue => issue.ComponentIndex)
            .ToList();
        return new ExpirationsAnalysisSessionSnapshot
        {
            Process = _process,
            SourcePath = _sourcePath,
            ReadOptions = _readOptions,
            ReadResult = _readResult,
            Analysis = analysis,
            Catalog = catalog,
            Distribution = _previewService.Build(analysis, catalog),
            PendingIssues = pending,
            ManualOverrides = _manualOverrides.ToList(),
            Messages = _readResult.Messages.Concat(_catalog.Warnings).Distinct().ToList()
        };
    }

    private ExpirationsBrokerComponentResolution? FindComponent(uint rowNumber, int componentIndex)
    {
        var row = Snapshot.Analysis?.RowResolutions.FirstOrDefault(value => value.RowNumber == rowNumber);
        return row is null || componentIndex < 0 || componentIndex >= row.Components.Count
            ? null
            : row.Components[componentIndex];
    }

    private bool IsActiveCatalogBroker(Guid brokerId) =>
        _catalog?.Items.Where(item => item.BrokerId == brokerId).All(item => item.IsActive) == true &&
        _catalog.Items.Any(item => item.BrokerId == brokerId);

    private void ClearAnalysis(bool preserveSourcePath)
    {
        if (!preserveSourcePath)
            _sourcePath = string.Empty;
        _readOptions = null;
        _readResult = null;
        _catalog = null;
        _associationDocuments = [];
        _manualOverrides.Clear();
        Snapshot = EmptySnapshot();
    }

    private ExpirationsAnalysisSessionSnapshot EmptySnapshot(
        ExpirationsWorkbookReadResult? readResult = null,
        IReadOnlyList<string>? messages = null) => new()
    {
        Process = _process,
        SourcePath = _sourcePath,
        ReadOptions = _readOptions,
        ReadResult = readResult,
        Messages = messages ?? []
    };

    private ExpirationsManualOverrideResult OverrideRejected(string message) => new()
    {
        Applied = false,
        Message = message,
        Snapshot = Snapshot
    };

    private ExpirationsAssociationConfirmationResult Confirmation(
        ExpirationsAssociationConfirmationOutcome outcome,
        string message) => new()
    {
        Outcome = outcome,
        Message = message,
        Snapshot = Snapshot
    };

    private static string StatusText(ExpirationsBrokerResolutionStatus status) => status switch
    {
        ExpirationsBrokerResolutionStatus.Unresolved => "No reconocido",
        ExpirationsBrokerResolutionStatus.Ambiguous => "Ambiguo",
        ExpirationsBrokerResolutionStatus.InactiveBroker => "Corredor inactivo",
        ExpirationsBrokerResolutionStatus.MissingBroker => "Sin corredor",
        _ => "Resuelto"
    };

    private static ExpirationsBrokerAssociation CopyAssociation(ExpirationsBrokerAssociation value) => new()
    {
        Id = value.Id,
        BrokerId = value.BrokerId,
        Kind = value.Kind,
        Value = value.Value,
        NormalizedValue = value.NormalizedValue,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc
    };
}
