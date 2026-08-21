namespace ECS.CommissionsMailer.Models.Expirations;

public sealed class ExpirationsSourceCell
{
    public int ColumnIndex { get; init; }
    public string ColumnReference { get; init; } = string.Empty;
    public string RawText { get; init; } = string.Empty;
    public string DisplayText { get; init; } = string.Empty;
}

public sealed class ExpirationsSourceRow
{
    public uint RowNumber { get; init; }
    public string RawBrokerValue { get; init; } = string.Empty;
    public IReadOnlyList<ExpirationsSourceCell> Cells { get; init; } = [];
}

public sealed class ExpirationsSourceWorkbook
{
    public string SourcePath { get; init; } = string.Empty;
    public string WorksheetName { get; init; } = string.Empty;
    public uint HeaderRowNumber { get; init; }
    public int BrokerColumnIndex { get; init; }
    public IReadOnlyList<ExpirationsSourceRow> Rows { get; init; } = [];
}

public sealed class ExpirationsWorkbookReadOptions
{
    public string? WorksheetName { get; init; }
    public uint? HeaderRowNumber { get; init; }
    public int? BrokerColumnIndex { get; init; }
}

public enum ExpirationsWorkbookReadStatus
{
    Success,
    HeaderNotFound,
    RequiresManualSelection,
    InvalidFile
}

public sealed class ExpirationsWorkbookSelectionCandidate
{
    public string WorksheetName { get; init; } = string.Empty;
    public uint HeaderRowNumber { get; init; }
    public int BrokerColumnIndex { get; init; }
    public string ColumnReference { get; init; } = string.Empty;
}

public sealed class ExpirationsWorkbookReadResult
{
    public ExpirationsWorkbookReadStatus Status { get; init; }
    public ExpirationsSourceWorkbook? Workbook { get; init; }
    public IReadOnlyList<ExpirationsWorkbookSelectionCandidate> Candidates { get; init; } = [];
    public IReadOnlyList<string> Messages { get; init; } = [];
    public bool IsSuccess => Status == ExpirationsWorkbookReadStatus.Success && Workbook is not null;
}

public sealed class ExpirationsBrokerComponent
{
    public string RawValue { get; init; } = string.Empty;
    public string NormalizedValue { get; init; } = string.Empty;
}

public enum ExpirationsBrokerResolutionStatus
{
    Resolved,
    Unresolved,
    Ambiguous,
    InactiveBroker,
    MissingBroker
}

public sealed class ExpirationsBrokerComponentResolution
{
    public string RawValue { get; init; } = string.Empty;
    public string NormalizedValue { get; init; } = string.Empty;
    public ExpirationsBrokerResolutionStatus Status { get; init; }
    public IReadOnlyList<Guid> CandidateBrokerIds { get; init; } = [];
    public Guid? ResolvedBrokerId { get; init; }
    public IReadOnlyList<Guid> MatchedAssociationIds { get; init; } = [];
    public IReadOnlyList<Guid> UnknownCatalogBrokerIds { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed class ExpirationsRowResolution
{
    public uint RowNumber { get; init; }
    public string RawBrokerValue { get; init; } = string.Empty;
    public IReadOnlyList<ExpirationsBrokerComponentResolution> Components { get; init; } = [];
    public IReadOnlyList<Guid> DistinctDestinationBrokerIds { get; init; } = [];
    public bool HasBlockingIssues { get; init; }
}

public sealed class ExpirationsWorkbookAnalysisResult
{
    public ExpirationsWorkbookReadStatus ReadStatus { get; init; }
    public int TotalRows { get; init; }
    public int ResolvedRows { get; init; }
    public int RowsWithBlockingIssues { get; init; }
    public int UnresolvedComponents { get; init; }
    public int AmbiguousComponents { get; init; }
    public int InactiveBrokerComponents { get; init; }
    public int MissingBrokerComponents { get; init; }
    public IReadOnlyList<ExpirationsRowResolution> RowResolutions { get; init; } = [];
    public IReadOnlyDictionary<Guid, IReadOnlyList<uint>> ResolvedRowNumbersByBroker { get; init; } =
        new Dictionary<Guid, IReadOnlyList<uint>>();
    public IReadOnlyList<string> Messages { get; init; } = [];
    public bool CanGenerate { get; init; }
}
