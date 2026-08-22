namespace ECS.CommissionsMailer.Models.Expirations;

public sealed record ExpirationsManualResolutionOverride(
    uint RowNumber,
    int ComponentIndex,
    Guid BrokerId);

public sealed class ExpirationsDistributionPreviewItem
{
    public Guid BrokerId { get; init; }
    public string BrokerName { get; init; } = string.Empty;
    public string PrimaryEmail { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public IReadOnlyList<string> DetectedValues { get; init; } = [];
    public string DetectedValuesText => string.Join(", ", DetectedValues);
}

public sealed class ExpirationsPendingIssue
{
    public uint RowNumber { get; init; }
    public int ComponentIndex { get; init; }
    public string RawValue { get; init; } = string.Empty;
    public string NormalizedValue { get; init; } = string.Empty;
    public ExpirationsBrokerResolutionStatus Status { get; init; }
    public string StatusText { get; init; } = string.Empty;
    public IReadOnlyList<Guid> CandidateBrokerIds { get; init; } = [];
    public string CandidateBrokerNames { get; init; } = string.Empty;
    public bool CanResolve => Status is ExpirationsBrokerResolutionStatus.Unresolved or
        ExpirationsBrokerResolutionStatus.Ambiguous;
}

public sealed class ExpirationsAnalysisSessionSnapshot
{
    public ExpirationsProcess? Process { get; init; }
    public string SourcePath { get; init; } = string.Empty;
    public ExpirationsWorkbookReadOptions? ReadOptions { get; init; }
    public ExpirationsWorkbookReadResult? ReadResult { get; init; }
    public string AnalyzedSourceSha256 { get; init; } = string.Empty;
    public ExpirationsWorkbookAnalysisResult? Analysis { get; init; }
    public IReadOnlyList<ExpirationsBrokerCatalogItem> Catalog { get; init; } = [];
    public IReadOnlyList<ExpirationsDistributionPreviewItem> Distribution { get; init; } = [];
    public IReadOnlyList<ExpirationsPendingIssue> PendingIssues { get; init; } = [];
    public IReadOnlyList<ExpirationsManualResolutionOverride> ManualOverrides { get; init; } = [];
    public IReadOnlyList<string> Messages { get; init; } = [];
    public bool RequiresWorkbookSelection => ReadResult?.Status is
        ExpirationsWorkbookReadStatus.HeaderNotFound or
        ExpirationsWorkbookReadStatus.RequiresManualSelection;
    public bool CanGenerate => Analysis?.CanGenerate == true;
}

public sealed class ExpirationsWorkbookInspection
{
    public string SourcePath { get; init; } = string.Empty;
    public IReadOnlyList<ExpirationsWorksheetInspection> Worksheets { get; init; } = [];
}

public sealed class ExpirationsWorksheetInspection
{
    public string WorksheetName { get; init; } = string.Empty;
    public IReadOnlyList<ExpirationsHeaderRowInspection> HeaderRows { get; init; } = [];
}

public sealed class ExpirationsHeaderRowInspection
{
    public uint RowNumber { get; init; }
    public IReadOnlyList<ExpirationsColumnInspection> Columns { get; init; } = [];
    public string DisplayText => $"Fila {RowNumber}: {string.Join(" | ", Columns.Select(column => column.DisplayText))}";
}

public sealed class ExpirationsColumnInspection
{
    public int ColumnIndex { get; init; }
    public string ColumnReference { get; init; } = string.Empty;
    public string HeaderText { get; init; } = string.Empty;
    public string DisplayText => $"{ColumnReference} — {(HeaderText.Length == 0 ? "(vacío)" : HeaderText)}";
}

public sealed record ExpirationsAssociationConfirmation(
    uint RowNumber,
    int ComponentIndex,
    Guid BrokerId,
    ExpirationsAssociationKind Kind);

public enum ExpirationsAssociationConfirmationOutcome
{
    Created,
    ExistingActive,
    Reactivated,
    SessionOverride,
    ConcurrencyConflict,
    Rejected,
    Failed
}

public sealed class ExpirationsAssociationConfirmationResult
{
    public ExpirationsAssociationConfirmationOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public ExpirationsAnalysisSessionSnapshot Snapshot { get; init; } = new();
    public bool Persisted => Outcome is ExpirationsAssociationConfirmationOutcome.Created or
        ExpirationsAssociationConfirmationOutcome.Reactivated;
}

public sealed class ExpirationsManualOverrideResult
{
    public bool Applied { get; init; }
    public string Message { get; init; } = string.Empty;
    public ExpirationsAnalysisSessionSnapshot Snapshot { get; init; } = new();
}

public sealed record ExpirationsProcessOption(ExpirationsProcess Value, string DisplayName);

public sealed record ExpirationsAssociationKindOption(ExpirationsAssociationKind Value, string DisplayName);

public sealed record ExpirationsBrokerChoice(Guid BrokerId, string Name, string PrimaryEmail)
{
    public string DisplayText => PrimaryEmail.Length == 0 ? Name : $"{Name} — {PrimaryEmail}";
}
