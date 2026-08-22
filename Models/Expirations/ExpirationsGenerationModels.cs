namespace ECS.CommissionsMailer.Models.Expirations;

public sealed class ExpirationsGenerationContext
{
    public ExpirationsGenerationContext(
        ExpirationsProcess process,
        string sourceWorkbookPath,
        string sourceWorkbookSha256,
        ExpirationsSourceWorkbook sourceWorkbook,
        ExpirationsWorkbookAnalysisResult analysis,
        IReadOnlyList<ExpirationsBrokerCatalogItem> brokerCatalog)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWorkbookPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceWorkbookSha256);
        ArgumentNullException.ThrowIfNull(sourceWorkbook);
        ArgumentNullException.ThrowIfNull(analysis);
        ArgumentNullException.ThrowIfNull(brokerCatalog);

        if (!analysis.CanGenerate || analysis.TotalRows <= 0)
            throw new ArgumentException("El análisis no está completo para generar.", nameof(analysis));
        if (sourceWorkbook.HeaderRowNumber == 0 || string.IsNullOrWhiteSpace(sourceWorkbook.WorksheetName))
            throw new ArgumentException("El workbook analizado no identifica una hoja y encabezado válidos.", nameof(sourceWorkbook));
        if (analysis.ResolvedRowNumbersByBroker.Count == 0 ||
            analysis.ResolvedRowNumbersByBroker.All(item => item.Value.Count == 0))
        {
            throw new ArgumentException("El análisis no contiene corredores destino.", nameof(analysis));
        }
        if (analysis.ResolvedRowNumbersByBroker.Any(item => item.Value.Count == 0))
            throw new ArgumentException("El análisis contiene un corredor destino sin filas.", nameof(analysis));
        var availableRows = sourceWorkbook.Rows.Select(row => row.RowNumber).ToHashSet();
        var distributedRows = analysis.ResolvedRowNumbersByBroker.Values
            .SelectMany(rows => rows)
            .Distinct()
            .ToList();
        if (distributedRows.Any(row => row <= sourceWorkbook.HeaderRowNumber || !availableRows.Contains(row)))
        {
            throw new ArgumentException(
                "La distribución contiene filas que no pertenecen al workbook analizado.",
                nameof(analysis));
        }

        var catalogById = brokerCatalog
            .GroupBy(item => item.BrokerId)
            .ToDictionary(group => group.Key, group => group.ToList());
        foreach (var brokerId in analysis.ResolvedRowNumbersByBroker.Keys)
        {
            if (!catalogById.TryGetValue(brokerId, out var matches) || matches.Count != 1)
                throw new ArgumentException($"El corredor destino '{brokerId:D}' no existe de forma única en el catálogo.", nameof(brokerCatalog));
            if (!matches[0].IsActive)
                throw new ArgumentException($"El corredor destino '{matches[0].Name}' está inactivo.", nameof(brokerCatalog));
        }

        Process = process;
        SourceWorkbookPath = Path.GetFullPath(sourceWorkbookPath);
        SourceWorkbookSha256 = sourceWorkbookSha256.Trim().ToLowerInvariant();
        SourceWorkbook = sourceWorkbook;
        Analysis = analysis;
        BrokerCatalog = brokerCatalog.ToArray();
    }

    public ExpirationsProcess Process { get; }
    public string SourceWorkbookPath { get; }
    public string SourceWorkbookSha256 { get; }
    public ExpirationsSourceWorkbook SourceWorkbook { get; }
    public ExpirationsWorkbookAnalysisResult Analysis { get; }
    public IReadOnlyList<ExpirationsBrokerCatalogItem> BrokerCatalog { get; }
}

public sealed record ExpirationsGenerationRequest(
    ExpirationsGenerationContext Context,
    string OutputParentDirectory,
    ExpirationsPeriod? Period = null,
    ExpirationsPremiumColumnOptions? PremiumColumnOptions = null);

public sealed class ExpirationsGenerationBatch
{
    public Guid Id { get; init; }
    public ExpirationsProcess Process { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string SourceWorkbookPath { get; init; } = string.Empty;
    public string SourceWorkbookSha256 { get; init; } = string.Empty;
    public string OutputDirectory { get; init; } = string.Empty;
    public IReadOnlyList<ExpirationsGeneratedFile> Files { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed class ExpirationsGeneratedFile
{
    public Guid BrokerId { get; init; }
    public string BrokerName { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public ExpirationsGeneratedFileVariant Variant { get; init; }
    public int RowCount { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public IReadOnlyList<uint> SourceRowNumbers { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool IsManuallyEdited { get; init; }
    public bool RequiresReview { get; init; }
}

public enum ExpirationsGeneratedFileVariant
{
    Standard,
    FelixAlphabetical,
    FelixExpirationDate,
    Manual
}

public sealed class ExpirationsGenerationPreparationResult
{
    public ExpirationsAnalysisSessionSnapshot Snapshot { get; init; } = new();
    public ExpirationsGenerationContext? Context { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public ExpirationsPremiumColumnResolution? PremiumColumnResolution { get; init; }
    public bool CanGenerate => Context is not null && ErrorMessage.Length == 0;
    public bool RequiresManualPremiumColumnSelection => PremiumColumnResolution?.Status is
        ExpirationsPremiumColumnResolutionStatus.MissingPremiumColumn or
        ExpirationsPremiumColumnResolutionStatus.MissingCurrencyColumn or
        ExpirationsPremiumColumnResolutionStatus.AmbiguousPremiumColumn or
        ExpirationsPremiumColumnResolutionStatus.AmbiguousCurrencyColumn or
        ExpirationsPremiumColumnResolutionStatus.InvalidPremiumColumnOverride or
        ExpirationsPremiumColumnResolutionStatus.InvalidCurrencyColumnOverride or
        ExpirationsPremiumColumnResolutionStatus.ConflictingColumnOverrides;
}

public sealed record ExpirationsGenerationProgress(
    int CurrentFile,
    int TotalFiles,
    string BrokerName);

public sealed record ExpirationsStandardWorkbookGenerationRequest(
    string SourceWorkbookPath,
    string DestinationWorkbookPath,
    string WorksheetName,
    uint HeaderRowNumber,
    IReadOnlyList<uint> SourceRowNumbers);

public class ExpirationsGenerationException : InvalidOperationException
{
    public ExpirationsGenerationException(string message) : base(message)
    {
    }

    public ExpirationsGenerationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
