namespace ECS.CommissionsMailer.Models;

public enum DeductionApplicationType
{
    GrossCommission,
    PayableAmount
}

public enum DeductionCurrency
{
    CRC,
    USD
}

public sealed class BrokerDeduction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Description { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DeductionCurrency Currency { get; set; }
    public DeductionApplicationType ApplicationType { get; set; }
    public string? TargetWorksheetName { get; set; }
    public int DisplayOrder { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string CurrencyText => Currency == DeductionCurrency.CRC ? "CRC — Colones" : "USD — Dólares";

    [System.Text.Json.Serialization.JsonIgnore]
    public string ApplicationTypeText => ApplicationType == DeductionApplicationType.GrossCommission
        ? "Monto bruto de comisión"
        : "Monto a pagar";

    [System.Text.Json.Serialization.JsonIgnore]
    public string TargetWorksheetText => string.IsNullOrWhiteSpace(TargetWorksheetName)
        ? "Sin asignar"
        : TargetWorksheetName;

    public bool AppliesToWorksheet(string? worksheetName) =>
        !string.IsNullOrWhiteSpace(TargetWorksheetName) &&
        string.Equals(
            TargetWorksheetName.Trim(),
            (worksheetName ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);

    public BrokerDeduction Clone() => new()
    {
        Id = Id,
        Description = Description,
        Amount = Amount,
        Currency = Currency,
        ApplicationType = ApplicationType,
        TargetWorksheetName = TargetWorksheetName,
        DisplayOrder = DisplayOrder
    };
}

public sealed class CommissionCurrencySummary
{
    public DeductionCurrency Currency { get; set; }
    public bool HasCommission { get; set; }
    public decimal GrossCommission { get; set; }
    public List<string> SourceCells { get; set; } = [];
}

public sealed class CommissionWorksheetAnalysis
{
    public string WorksheetName { get; set; } = string.Empty;
    public string AnalyzerName { get; set; } = string.Empty;
    public CommissionCurrencySummary Crc { get; set; } = new() { Currency = DeductionCurrency.CRC };
    public CommissionCurrencySummary Usd { get; set; } = new() { Currency = DeductionCurrency.USD };
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Errors.Count == 0;
}

public sealed class WorkbookAnalysisResult
{
    public string WorkbookPath { get; set; } = string.Empty;
    public string WorkbookSha256 { get; set; } = string.Empty;
    public List<CommissionWorksheetAnalysis> Worksheets { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Errors.Count == 0 && Worksheets.Count > 0 && Worksheets.All(value => value.IsValid);
}

public sealed class AppliedDeductionSnapshot
{
    public Guid Id { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal ConfiguredAmount { get; set; }
    public decimal AppliedAmount { get; set; }
    public DeductionCurrency Currency { get; set; }
    public DeductionApplicationType ApplicationType { get; set; }
    public string TargetWorksheetName { get; set; } = string.Empty;
    public int DisplayOrder { get; set; }
}

public sealed class PaymentCurrencyCalculation
{
    public DeductionCurrency Currency { get; set; }
    public bool HasCommission { get; set; }
    public bool MinimumApplied { get; set; }
    public decimal MinimumAmount { get; set; }
    public decimal GrossCommissionOriginal { get; set; }
    public decimal GrossDeductions { get; set; }
    public decimal AdjustedGrossCommission { get; set; }
    public decimal Vat { get; set; }
    public decimal InvoiceAmount { get; set; }
    public decimal Withholding { get; set; }
    public decimal PayableBeforeFinalDeductions { get; set; }
    public decimal FinalDeductions { get; set; }
    public decimal DepositedAmount { get; set; }
    public string Observation { get; set; } = string.Empty;
    public List<AppliedDeductionSnapshot> Deductions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Errors.Count == 0;
}

public sealed class PaymentCalculationResult
{
    public PaymentCurrencyCalculation Crc { get; set; } = new() { Currency = DeductionCurrency.CRC };
    public PaymentCurrencyCalculation Usd { get; set; } = new() { Currency = DeductionCurrency.USD };
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => Crc.IsValid && Usd.IsValid;

    [System.Text.Json.Serialization.JsonIgnore]
    public IEnumerable<string> Errors => Crc.Errors.Concat(Usd.Errors);
}

public enum PaymentGenerationStatus
{
    Generated,
    ReadyToSend,
    Sent,
    PartialSend,
    Failed
}

public sealed class GeneratedPaymentFile
{
    public Guid BrokerId { get; set; }
    public string BrokerName { get; set; } = string.Empty;
    public string WorksheetName { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public string AnalyzerName { get; set; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; set; }
    public PaymentCurrencyCalculation Crc { get; set; } = new() { Currency = DeductionCurrency.CRC };
    public PaymentCurrencyCalculation Usd { get; set; } = new() { Currency = DeductionCurrency.USD };
}

public sealed class PaymentGenerationBatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Period { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string SourceWorkbookPath { get; set; } = string.Empty;
    public string SourceWorkbookSha256 { get; set; } = string.Empty;
    public string OutputDirectory { get; set; } = string.Empty;
    public PaymentGenerationStatus Status { get; set; } = PaymentGenerationStatus.Generated;
    public List<GeneratedPaymentFile> Files { get; set; } = [];
    public List<Guid> SentBrokerIds { get; set; } = [];
    public List<Guid> FailedBrokerIds { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed record WorksheetBrokerAssignment(string WorksheetName, Broker Broker);

public sealed class WorksheetBrokerMappingResult
{
    public List<WorksheetBrokerAssignment> Assignments { get; set; } = [];
    public List<string> MissingWorksheetNames { get; set; } = [];
    public List<string> Errors { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsValid => MissingWorksheetNames.Count == 0 && Errors.Count == 0;
}

public sealed class PaymentGenerationRequest
{
    public required string SourceWorkbookPath { get; init; }
    public required string Period { get; init; }
    public required string OutputDirectory { get; init; }
    public required WorkbookAnalysisResult Analysis { get; init; }
    public required IReadOnlyList<WorksheetBrokerAssignment> Assignments { get; init; }
    public bool ReplaceExistingFiles { get; init; }
}
