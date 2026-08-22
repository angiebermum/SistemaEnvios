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

public sealed class ExpirationsExcludedValueSummary
{
    public string RawValue { get; init; } = string.Empty;
    public string NormalizedValue { get; init; } = string.Empty;
    public int RowCount { get; init; }
    public string StatusText => "No distribuir";
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
    public IReadOnlyList<ExpirationsExcludedValueSummary> ExcludedValues { get; init; } = [];
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

public sealed class ExpirationsExclusionConfirmationResult
{
    public bool Persisted { get; init; }
    public string Message { get; init; } = string.Empty;
    public ExpirationsAnalysisSessionSnapshot Snapshot { get; init; } = new();
}

public sealed record ExpirationsProcessOption(ExpirationsProcess Value, string DisplayName);

public sealed record ExpirationsAssociationKindOption(ExpirationsAssociationKind Value, string DisplayName);

public sealed record ExpirationsBrokerChoice(Guid BrokerId, string Name, string PrimaryEmail)
{
    public string DisplayText => PrimaryEmail.Length == 0 ? Name : $"{Name} — {PrimaryEmail}";
}

public sealed class ExpirationsAssociationAdministrationItem
{
    public ExpirationsBrokerAssociation Association { get; init; } = new();
    public string UpdateTime { get; init; } = string.Empty;
    public string BrokerName { get; init; } = string.Empty;
    public string BrokerPrimaryEmail { get; init; } = string.Empty;
    public string KindText => Association.Kind switch
    {
        ExpirationsAssociationKind.Name => "Nombre",
        ExpirationsAssociationKind.Alias => "Alias",
        ExpirationsAssociationKind.Code => "Código",
        _ => Association.Kind.ToString()
    };
    public string OriginText => Association.Origin switch
    {
        ExpirationsAssociationOrigin.Imported => "Importado",
        ExpirationsAssociationOrigin.ManuallyConfirmed => "Confirmado manualmente",
        ExpirationsAssociationOrigin.ManuallyAdded => "Agregado manualmente",
        _ => "Confirmado"
    };
    public string StatusText => Association.IsActive ? "Activa" : "Inactiva";
}

public sealed class ExpirationsObservedIdentifierAdministrationItem
{
    public ExpirationsObservedIdentifier Identifier { get; init; } = new();
    public string UpdateTime { get; init; } = string.Empty;
    public string BrokerName { get; init; } = string.Empty;
    public string BrokerPrimaryEmail { get; init; } = string.Empty;
}

public sealed class ExpirationsKnownIdentifierAdministrationItem
{
    public Guid BrokerId { get; init; }
    public string BrokerName { get; init; } = string.Empty;
    public string BrokerPrimaryEmail { get; init; } = string.Empty;
    public ExpirationsAssociationKind Kind { get; init; }
    public string Value { get; init; } = string.Empty;
    public string NormalizedValue { get; init; } = string.Empty;
    public string OriginText { get; init; } = string.Empty;
    public string StatusText { get; init; } = string.Empty;
    public DateTimeOffset? UpdatedAtUtc { get; init; }
    public ExpirationsAssociationAdministrationItem? AssociationItem { get; init; }
    public ExpirationsObservedIdentifierAdministrationItem? ObservedItem { get; init; }
    public bool IsMaster { get; init; }
    public string KindText => Kind switch
    {
        ExpirationsAssociationKind.Name => "Nombre",
        ExpirationsAssociationKind.Alias => "Alias",
        ExpirationsAssociationKind.Code => "Código",
        _ => Kind.ToString()
    };
    public bool IsAssociation => AssociationItem is not null;
    public bool IsObserved => ObservedItem is not null;
}

public sealed class ExpirationsExclusionAdministrationItem
{
    public ExpirationsExclusion Exclusion { get; init; } = new();
    public string UpdateTime { get; init; } = string.Empty;
    public string StatusText => Exclusion.IsActive ? "Activa" : "Inactiva";
}

public enum ExpirationsRoutingAdministrationOutcome
{
    Updated,
    ConcurrencyConflict,
    Conflict,
    NotFound,
    Rejected
}

public sealed class ExpirationsRoutingAdministrationResult
{
    public ExpirationsRoutingAdministrationOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool WasPersisted => Outcome == ExpirationsRoutingAdministrationOutcome.Updated;
}
