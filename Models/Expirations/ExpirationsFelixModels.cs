namespace ECS.CommissionsMailer.Models.Expirations;

public enum ExpirationsFelixWorkbookVariant
{
    Alphabetical,
    ExpirationDate
}

public sealed record ExpirationsFelixTemplateColumn(
    string TargetHeader,
    string SourceHeader,
    double Width,
    string NumberFormat = "General");

public sealed class ExpirationsFelixRow
{
    public uint SourceRowNumber { get; init; }
    public string PolicyNumber { get; init; } = string.Empty;
    public string InsuredName { get; init; } = string.Empty;
    public string Insurer { get; init; } = string.Empty;
    public double ExpirationDateSerial { get; init; }
    public DateTime ExpirationDate { get; init; }
    public double Premium { get; init; }
    public string PaymentPeriod { get; init; } = string.Empty;
    public string Plate { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public bool IsIns { get; init; }
}

public sealed class ExpirationsFelixGenerationPlan
{
    public ExpirationsPeriod Period { get; init; } = new(2000, 1);
    public IReadOnlyList<ExpirationsFelixRow> CanonicalRows { get; init; } = [];
}

public sealed record ExpirationsFelixWorkbookGenerationRequest(
    string DestinationWorkbookPath,
    ExpirationsFelixGenerationPlan Plan,
    ExpirationsFelixWorkbookVariant Variant);

public sealed class ExpirationsNextMonthPreflightResult
{
    public ExpirationsPremiumColumnResolution PremiumColumns { get; init; } = new();
    public IReadOnlyDictionary<Guid, ExpirationsPremiumTotalsPlan> PremiumTotalsPlansByBrokerId { get; init; } =
        new Dictionary<Guid, ExpirationsPremiumTotalsPlan>();
    public Guid SpecialBrokerId { get; init; }
    public ExpirationsFelixGenerationPlan? FelixPlan { get; init; }
}

public sealed record ExpirationsGeneratedFileNameRequest(
    Guid BrokerId,
    string BrokerName,
    ExpirationsGeneratedFileVariant Variant);

public sealed record ExpirationsGeneratedFileNameKey(
    Guid BrokerId,
    ExpirationsGeneratedFileVariant Variant);

public sealed record ExpirationsMonthOption(int Month, string DisplayName);

public sealed class ExpirationsPremiumColumnResolutionException : ExpirationsGenerationException
{
    public ExpirationsPremiumColumnResolutionException(
        string message,
        ExpirationsPremiumColumnResolution resolution) : base(message)
    {
        Resolution = resolution;
    }

    public ExpirationsPremiumColumnResolution Resolution { get; }
}
