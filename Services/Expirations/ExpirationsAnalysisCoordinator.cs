using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsAnalysisCoordinator
{
    ExpirationsAnalysisSessionSnapshot Snapshot { get; }
    void ResetPreparation();
    void SelectProcess(ExpirationsProcess? process);
    void SelectFile(string sourcePath);
    Task<ExpirationsAnalysisSessionSnapshot> AnalyzeAsync(
        ExpirationsWorkbookReadOptions? options = null,
        CancellationToken cancellationToken = default);
    Task<ExpirationsAnalysisSessionSnapshot> RefreshCatalogAndReanalyzeAsync(
        CancellationToken cancellationToken = default);
    Task<ExpirationsGenerationPreparationResult> PrepareGenerationAsync(
        CancellationToken cancellationToken = default);
    Task<ExpirationsGenerationPreparationResult> PrepareGenerationAsync(
        ExpirationsPeriod period,
        ExpirationsPremiumColumnOptions? premiumColumnOptions,
        CancellationToken cancellationToken = default);
    Task<ExpirationsWorkbookInspection> InspectWorkbookAsync(CancellationToken cancellationToken = default);
    ExpirationsManualOverrideResult ApplyManualOverride(
        uint rowNumber,
        int componentIndex,
        Guid brokerId);
    ExpirationsManualOverrideResult ApplyManualOverrideWithDestination(
        uint rowNumber,
        int componentIndex,
        Guid brokerId,
        ExpirationsDestinationGroup destinationGroup) =>
        ApplyManualOverride(rowNumber, componentIndex, brokerId);
    Task<ExpirationsAssociationConfirmationResult> ConfirmAssociationAsync(
        ExpirationsAssociationConfirmation confirmation,
        CancellationToken cancellationToken = default);
    Task<ExpirationsExclusionConfirmationResult> ExcludeAsync(
        uint rowNumber,
        int componentIndex,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsAnalysisCoordinator : IExpirationsAnalysisCoordinator
{
    private readonly ExpirationsBrokerCatalogService _catalogService;
    private readonly IExpirationsBrokerAssociationRepository _associations;
    private readonly IExpirationsExclusionRepository? _exclusions;
    private readonly IExpirationsWorkbookReader _reader;
    private readonly ExpirationsWorkbookAnalysisService _analysisService;
    private readonly ExpirationsDistributionPreviewService _previewService;
    private readonly IExpirationsWorkbookInspectionService _inspectionService;
    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, string> _computeSourceSha256;
    private readonly IExpirationsNextMonthGenerationPreflightService _nextMonthPreflightService;
    private readonly IExpirationsObservedIdentifierCaptureService? _observedIdentifierCapture;
    private ExpirationsProcess? _process;
    private string _sourcePath = string.Empty;
    private ExpirationsWorkbookReadOptions? _readOptions;
    private ExpirationsWorkbookReadResult? _readResult;
    private ExpirationsBrokerCatalog? _catalog;
    private IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>> _associationDocuments = [];
    private IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>> _exclusionDocuments = [];
    private readonly List<ExpirationsManualResolutionOverride> _manualOverrides = [];
    private string _analyzedSourceSha256 = string.Empty;
    private IReadOnlyList<string> _observationWarnings = [];

    public ExpirationsAnalysisCoordinator(
        ExpirationsBrokerCatalogService catalogService,
        IExpirationsBrokerAssociationRepository associations,
        IExpirationsWorkbookReader? reader = null,
        ExpirationsWorkbookAnalysisService? analysisService = null,
        ExpirationsDistributionPreviewService? previewService = null,
        IExpirationsWorkbookInspectionService? inspectionService = null,
        ExpirationsBrokerNormalizer? normalizer = null,
        TimeProvider? timeProvider = null,
        Func<string, string>? sourceHashProvider = null,
        IExpirationsNextMonthGenerationPreflightService? nextMonthPreflightService = null,
        IExpirationsExclusionRepository? exclusions = null,
        IExpirationsObservedIdentifierCaptureService? observedIdentifierCapture = null)
    {
        _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
        _associations = associations ?? throw new ArgumentNullException(nameof(associations));
        _exclusions = exclusions;
        _reader = reader ?? new ExpirationsWorkbookReader();
        _analysisService = analysisService ?? new ExpirationsWorkbookAnalysisService();
        _previewService = previewService ?? new ExpirationsDistributionPreviewService();
        _inspectionService = inspectionService ?? new ExpirationsWorkbookInspectionService();
        _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
        var hashService = new GeneratedFileHashService();
        _computeSourceSha256 = sourceHashProvider ?? hashService.ComputeSha256;
        _nextMonthPreflightService = nextMonthPreflightService ??
            new ExpirationsNextMonthGenerationPreflightService();
        _observedIdentifierCapture = observedIdentifierCapture;
        Snapshot = EmptySnapshot();
    }

    public ExpirationsAnalysisSessionSnapshot Snapshot { get; private set; }

    public void ResetPreparation()
    {
        _process = null;
        ClearAnalysis(preserveSourcePath: false);
    }

    public void SelectProcess(ExpirationsProcess? process)
    {
        if (_process == process)
            return;
        _process = process;
        ClearAnalysis(preserveSourcePath: true, preserveCatalog: true);
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
        _observationWarnings = [];
        var readResult = await Task.Run(
            () => _reader.Read(_sourcePath, options),
            cancellationToken);
        _readResult = readResult;
        if (!readResult.IsSuccess)
        {
            _analyzedSourceSha256 = string.Empty;
            _catalog = null;
            _associationDocuments = [];
            Snapshot = EmptySnapshot(readResult, readResult.Messages);
            return Snapshot;
        }

        var analyzedHash = _computeSourceSha256(_sourcePath);

        var catalogTask = _catalogService.LoadAsync(cancellationToken);
        var associationsTask = _associations.ListAsync(cancellationToken);
        var exclusionsTask = ListExclusionsAsync(cancellationToken);
        await Task.WhenAll(catalogTask, associationsTask, exclusionsTask);
        _catalog = await catalogTask;
        _associationDocuments = await associationsTask;
        _exclusionDocuments = await exclusionsTask;
        var completedHash = _computeSourceSha256(_sourcePath);
        if (!string.Equals(analyzedHash, completedHash, StringComparison.OrdinalIgnoreCase))
        {
            _analyzedSourceSha256 = string.Empty;
            throw new ExpirationsGenerationException(
                "El archivo seleccionado cambió durante el análisis. Analícelo nuevamente.");
        }
        _analyzedSourceSha256 = completedHash;
        Snapshot = BuildSnapshot();
        if (_observedIdentifierCapture is not null && Snapshot.Analysis is { } analysis)
        {
            try
            {
                await _observedIdentifierCapture.CaptureAsync(analysis, Snapshot.Catalog, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _observationWarnings =
                    [$"El análisis terminó, pero no fue posible registrar los identificadores observados: {ex.Message}"];
                Snapshot = BuildSnapshot();
            }
        }
        return Snapshot;
    }

    public async Task<ExpirationsGenerationPreparationResult> PrepareGenerationAsync(
        CancellationToken cancellationToken = default)
        => await PrepareGenerationCoreAsync(null, null, validateNextMonth: false, cancellationToken);

    public async Task<ExpirationsGenerationPreparationResult> PrepareGenerationAsync(
        ExpirationsPeriod period,
        ExpirationsPremiumColumnOptions? premiumColumnOptions,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(period);
        return await PrepareGenerationCoreAsync(
            period,
            premiumColumnOptions,
            validateNextMonth: true,
            cancellationToken);
    }

    private async Task<ExpirationsGenerationPreparationResult> PrepareGenerationCoreAsync(
        ExpirationsPeriod? period,
        ExpirationsPremiumColumnOptions? premiumColumnOptions,
        bool validateNextMonth,
        CancellationToken cancellationToken)
    {
        var snapshot = await RefreshCatalogAndReanalyzeAsync(cancellationToken);
        if (snapshot.Process is null)
            return PreparationRejected(snapshot, "Seleccione el proceso de Vencimientos antes de generar.");
        if (snapshot.ReadResult?.Workbook is not { } sourceWorkbook || snapshot.Analysis is not { } analysis)
            return PreparationRejected(snapshot, "Analice el archivo antes de generar.");
        if (!analysis.CanGenerate || analysis.TotalRows <= 0)
            return PreparationRejected(snapshot, "El análisis contiene pendientes o no tiene registros para generar.");
        if (analysis.ResolvedRowNumbersByBroker.Count == 0 ||
            analysis.ResolvedRowNumbersByBroker.All(item => item.Value.Count == 0))
        {
            return PreparationRejected(snapshot, "El análisis no contiene corredores destino.");
        }
        if (string.IsNullOrWhiteSpace(_analyzedSourceSha256))
            return PreparationRejected(snapshot, "El archivo no tiene un hash de análisis válido. Analícelo nuevamente.");

        var currentHash = _computeSourceSha256(_sourcePath);
        if (!string.Equals(currentHash, _analyzedSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            return PreparationRejected(
                snapshot,
                "El archivo seleccionado cambió después del análisis.\nAnalícelo nuevamente antes de generar.");
        }

        try
        {
            var context = new ExpirationsGenerationContext(
                snapshot.Process.Value,
                _sourcePath,
                _analyzedSourceSha256,
                sourceWorkbook,
                analysis,
                snapshot.Catalog);
            if (validateNextMonth && context.Process == ExpirationsProcess.NextMonth)
            {
                _ = await Task.Run(
                    () => _nextMonthPreflightService.Validate(
                        context,
                        period,
                        premiumColumnOptions,
                        cancellationToken),
                    cancellationToken);
            }
            return new ExpirationsGenerationPreparationResult
            {
                Snapshot = snapshot,
                Context = context
            };
        }
        catch (ExpirationsPremiumColumnResolutionException ex)
        {
            return PreparationRejected(snapshot, ex.Message, ex.Resolution);
        }
        catch (ExpirationsGenerationException ex)
        {
            return PreparationRejected(snapshot, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return PreparationRejected(snapshot, ex.Message);
        }
    }

    public async Task<ExpirationsAnalysisSessionSnapshot> RefreshCatalogAndReanalyzeAsync(
        CancellationToken cancellationToken = default)
    {
        var catalogTask = _catalogService.LoadAsync(cancellationToken);
        var associationsTask = _associations.ListAsync(cancellationToken);
        var exclusionsTask = ListExclusionsAsync(cancellationToken);
        await Task.WhenAll(catalogTask, associationsTask, exclusionsTask);
        _catalog = await catalogTask;
        _associationDocuments = await associationsTask;
        _exclusionDocuments = await exclusionsTask;
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
        Guid brokerId) => ApplyManualOverrideWithDestination(
            rowNumber,
            componentIndex,
            brokerId,
            ExpirationsDestinationGroup.Principal);

    public ExpirationsManualOverrideResult ApplyManualOverrideWithDestination(
        uint rowNumber,
        int componentIndex,
        Guid brokerId,
        ExpirationsDestinationGroup destinationGroup)
    {
        if (_readResult is null || _catalog is null || Snapshot.Analysis is null)
            return OverrideRejected("No existe un análisis activo para aplicar la selección.");
        if (!IsActiveCatalogBroker(brokerId))
            return OverrideRejected("El corredor seleccionado no existe o está inactivo para Vencimientos.");
        if (!new ExpirationsDestinationRoutingPolicy(_catalog.Items).IsAllowed(brokerId, destinationGroup))
            return OverrideRejected("El archivo destino seleccionado no corresponde al corredor.");
        var component = FindComponent(rowNumber, componentIndex);
        if (component is null || component.Status is not (
                ExpirationsBrokerResolutionStatus.Ambiguous or
                ExpirationsBrokerResolutionStatus.Unresolved))
        {
            return OverrideRejected("El elemento seleccionado no admite una resolución manual de sesión.");
        }

        _manualOverrides.RemoveAll(value =>
            value.RowNumber == rowNumber && value.ComponentIndex == componentIndex);
        _manualOverrides.Add(new ExpirationsManualResolutionOverride(
            rowNumber,
            componentIndex,
            brokerId,
            destinationGroup));
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
        if (!new ExpirationsDestinationRoutingPolicy(_catalog.Items)
            .IsAllowed(confirmation.BrokerId, confirmation.DestinationGroup))
        {
            return Confirmation(
                ExpirationsAssociationConfirmationOutcome.Rejected,
                "El archivo destino seleccionado no corresponde al corredor.");
        }

        var normalizedValue = _normalizer.Normalize(component.RawValue);
        if (normalizedValue.Length == 0)
        {
            return Confirmation(ExpirationsAssociationConfirmationOutcome.Rejected,
                "No se puede crear una asociación para un valor vacío.");
        }

        try
        {
            var latestExclusions = await ListExclusionsAsync(cancellationToken);
            if (latestExclusions.Any(document =>
                    document.Value.IsActive &&
                    _normalizer.Normalize(document.Value.NormalizedValue.Length > 0
                        ? document.Value.NormalizedValue
                        : document.Value.Value) == normalizedValue))
            {
                _exclusionDocuments = latestExclusions;
                Snapshot = BuildSnapshot();
                return Confirmation(
                    ExpirationsAssociationConfirmationOutcome.Rejected,
                    "Este valor está marcado como No distribuir. Desactive o corrija la exclusión antes de asociarlo.");
            }

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
                var manual = ApplyManualOverrideWithDestination(
                    confirmation.RowNumber,
                    confirmation.ComponentIndex,
                    confirmation.BrokerId,
                    confirmation.DestinationGroup);
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
                reactivated.DestinationGroup = confirmation.DestinationGroup;
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
                Origin = ExpirationsAssociationOrigin.ManuallyConfirmed,
                DestinationGroup = confirmation.DestinationGroup,
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

    public async Task<ExpirationsExclusionConfirmationResult> ExcludeAsync(
        uint rowNumber,
        int componentIndex,
        CancellationToken cancellationToken = default)
    {
        if (_exclusions is null)
            return ExclusionResult(false, "La persistencia de exclusiones no está disponible.");
        var component = FindComponent(rowNumber, componentIndex);
        if (component is null || component.Status is ExpirationsBrokerResolutionStatus.Resolved or
            ExpirationsBrokerResolutionStatus.Excluded || string.IsNullOrWhiteSpace(component.RawValue))
        {
            return ExclusionResult(false, "El valor seleccionado no puede marcarse como No distribuir.");
        }

        var normalizedValue = _normalizer.Normalize(component.RawValue);
        if (normalizedValue.Length == 0)
            return ExclusionResult(false, "No se puede excluir un valor vacío.");

        try
        {
            var associationsTask = _associations.ListAsync(cancellationToken);
            var exclusionsTask = _exclusions.ListAsync(cancellationToken);
            await Task.WhenAll(associationsTask, exclusionsTask);
            var associations = await associationsTask;
            var exclusions = await exclusionsTask;
            var associationConflict = associations.FirstOrDefault(document =>
                    document.Value.IsActive &&
                    _normalizer.Normalize(document.Value.NormalizedValue.Length > 0
                        ? document.Value.NormalizedValue
                        : document.Value.Value) == normalizedValue);
            if (associationConflict is not null)
            {
                _associationDocuments = associations;
                _exclusionDocuments = exclusions;
                Snapshot = BuildSnapshot();
                return ExclusionResult(
                    false,
                    $"Este valor ya está asociado a " +
                    $"{_catalog?.Items.FirstOrDefault(item => item.BrokerId == associationConflict.Value.BrokerId)?.Name ?? associationConflict.Value.BrokerId.ToString("D")}. " +
                    "Desactive o corrija esa asociación primero.");
            }

            var exact = exclusions
                .Where(document => _normalizer.Normalize(document.Value.NormalizedValue.Length > 0
                    ? document.Value.NormalizedValue
                    : document.Value.Value) == normalizedValue)
                .OrderBy(document => document.Value.Id)
                .ToList();
            if (exact.Any(document => document.Value.IsActive))
            {
                _associationDocuments = associations;
                _exclusionDocuments = exclusions;
                Snapshot = BuildSnapshot();
                return ExclusionResult(false, "El valor ya está marcado como No distribuir.");
            }

            var now = _timeProvider.GetUtcNow();
            if (exact.FirstOrDefault() is { } inactive)
            {
                var value = CopyExclusion(inactive.Value);
                value.Value = component.RawValue;
                value.NormalizedValue = normalizedValue;
                value.IsActive = true;
                value.UpdatedAtUtc = now;
                await _exclusions.UpdateAsync(value, inactive.UpdateTime, cancellationToken);
            }
            else
            {
                await _exclusions.CreateAsync(new ExpirationsExclusion
                {
                    Id = Guid.NewGuid(),
                    Value = component.RawValue,
                    NormalizedValue = normalizedValue,
                    IsActive = true,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now
                }, cancellationToken);
            }

            _associationDocuments = await _associations.ListAsync(cancellationToken);
            _exclusionDocuments = await _exclusions.ListAsync(cancellationToken);
            _manualOverrides.RemoveAll(value =>
                value.RowNumber == rowNumber && value.ComponentIndex == componentIndex);
            Snapshot = BuildSnapshot();
            return ExclusionResult(
                true,
                "El valor quedó marcado como No distribuir y el análisis fue actualizado.");
        }
        catch (FirestoreConcurrencyException)
        {
            _associationDocuments = await _associations.ListAsync(cancellationToken);
            _exclusionDocuments = await _exclusions.ListAsync(cancellationToken);
            Snapshot = BuildSnapshot();
            return ExclusionResult(
                false,
                "La información cambió mientras se guardaba. Revise el pendiente nuevamente.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ExclusionResult(false, $"No fue posible guardar la exclusión: {ex.Message}");
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
            _manualOverrides,
            _exclusionDocuments.Select(document => document.Value));
        var names = catalog.GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.First().Name);
        var pending = analysis.RowResolutions
            .SelectMany(row => row.Components.Select((component, index) => new
            {
                row.RowNumber,
                ComponentIndex = index,
                Component = component
            }))
            .Where(item => item.Component.Status is not (
                ExpirationsBrokerResolutionStatus.Resolved or
                ExpirationsBrokerResolutionStatus.Excluded))
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
        var excluded = analysis.RowResolutions
            .SelectMany(row => row.Components
                .Where(component => component.Status == ExpirationsBrokerResolutionStatus.Excluded)
                .Select(component => new { row.RowNumber, Component = component }))
            .GroupBy(item => item.Component.NormalizedValue, StringComparer.Ordinal)
            .Select(group => new ExpirationsExcludedValueSummary
            {
                RawValue = group.Select(item => item.Component.RawValue).FirstOrDefault() ?? string.Empty,
                NormalizedValue = group.Key,
                RowCount = group.Select(item => item.RowNumber).Distinct().Count()
            })
            .OrderBy(item => item.RawValue, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ExpirationsAnalysisSessionSnapshot
        {
            Process = _process,
            SourcePath = _sourcePath,
            ReadOptions = _readOptions,
            ReadResult = _readResult,
            AnalyzedSourceSha256 = _analyzedSourceSha256,
            Analysis = analysis,
            Catalog = catalog,
            Distribution = _previewService.Build(analysis, catalog),
            PendingIssues = pending,
            ExcludedValues = excluded,
            ManualOverrides = _manualOverrides.ToList(),
            Messages = _readResult.Messages
                .Concat(_catalog.Warnings)
                .Concat(_observationWarnings)
                .Distinct()
                .ToList()
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

    private void ClearAnalysis(bool preserveSourcePath, bool preserveCatalog = false)
    {
        if (!preserveSourcePath)
            _sourcePath = string.Empty;
        _readOptions = null;
        _readResult = null;
        if (!preserveCatalog)
        {
            _catalog = null;
            _associationDocuments = [];
            _exclusionDocuments = [];
        }
        _manualOverrides.Clear();
        _analyzedSourceSha256 = string.Empty;
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
        AnalyzedSourceSha256 = _analyzedSourceSha256,
        Catalog = _catalog?.Items.ToList() ?? [],
        Messages = messages ?? []
    };

    private static ExpirationsGenerationPreparationResult PreparationRejected(
        ExpirationsAnalysisSessionSnapshot snapshot,
        string message,
        ExpirationsPremiumColumnResolution? premiumColumnResolution = null) => new()
    {
        Snapshot = snapshot,
        ErrorMessage = message,
        PremiumColumnResolution = premiumColumnResolution
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

    private ExpirationsExclusionConfirmationResult ExclusionResult(bool persisted, string message) => new()
    {
        Persisted = persisted,
        Message = message,
        Snapshot = Snapshot
    };

    private Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>> ListExclusionsAsync(
        CancellationToken cancellationToken) =>
        _exclusions is null
            ? Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>>([])
            : _exclusions.ListAsync(cancellationToken);

    private static string StatusText(ExpirationsBrokerResolutionStatus status) => status switch
    {
        ExpirationsBrokerResolutionStatus.Unresolved => "No reconocido",
        ExpirationsBrokerResolutionStatus.Excluded => "No distribuir",
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
        Origin = value.Origin,
        DestinationGroup = value.DestinationGroup,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc
    };

    private static ExpirationsExclusion CopyExclusion(ExpirationsExclusion value) => new()
    {
        Id = value.Id,
        Value = value.Value,
        NormalizedValue = value.NormalizedValue,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc
    };
}
