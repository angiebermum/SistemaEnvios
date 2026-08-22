namespace ECS.CommissionsMailer.Models.Expirations;

public enum ExpirationsCurrency
{
    Unsupported,
    Crc,
    Usd
}

public enum ExpirationsPremiumColumnResolutionStatus
{
    Success,
    WorksheetNotFound,
    HeaderRowNotFound,
    MissingPremiumColumn,
    MissingCurrencyColumn,
    AmbiguousPremiumColumn,
    AmbiguousCurrencyColumn,
    InvalidPremiumColumnOverride,
    InvalidCurrencyColumnOverride,
    ConflictingColumnOverrides
}

public sealed record ExpirationsPremiumColumnOptions(
    int? PremiumColumnIndex = null,
    int? CurrencyColumnIndex = null);

public sealed class ExpirationsPremiumColumnResolution
{
    public ExpirationsPremiumColumnResolutionStatus Status { get; init; }
    public int PremiumColumnIndex { get; init; }
    public string PremiumColumnReference { get; init; } = string.Empty;
    public int CurrencyColumnIndex { get; init; }
    public string CurrencyColumnReference { get; init; } = string.Empty;
    public bool IsSuccess => Status == ExpirationsPremiumColumnResolutionStatus.Success;
}

public enum ExpirationsPremiumCellKind
{
    Numeric,
    Missing,
    Text,
    Formula
}

public sealed class ExpirationsPremiumRowInspection
{
    public uint RowNumber { get; init; }
    public string RawCurrencyText { get; init; } = string.Empty;
    public bool CurrencyHasFormula { get; init; }
    public ExpirationsPremiumCellKind PremiumCellKind { get; init; }
    public string RawPremiumText { get; init; } = string.Empty;
    public double? NumericPremiumValue { get; init; }
}

public sealed class ExpirationsPremiumTotalsPlan
{
    public string WorksheetName { get; init; } = string.Empty;
    public string PremiumColumnReference { get; init; } = string.Empty;
    public string CurrencyColumnReference { get; init; } = string.Empty;
    public uint HeaderRowNumber { get; init; }
    public uint DataFirstRow { get; init; }
    public uint DataLastRow { get; init; }
    public int RowCount { get; init; }
    public IReadOnlyList<string> ObservedCrcLabels { get; init; } = [];
    public IReadOnlyList<string> ObservedUsdLabels { get; init; } = [];
    public string CrcFormula { get; init; } = string.Empty;
    public string UsdFormula { get; init; } = string.Empty;
}

public sealed record ExpirationsNextMonthStandardWorkbookGenerationRequest(
    string SourceWorkbookPath,
    string DestinationWorkbookPath,
    string WorksheetName,
    uint HeaderRowNumber,
    IReadOnlyList<uint> SourceRowNumbers,
    ExpirationsPremiumColumnOptions? PremiumColumnOptions = null);
