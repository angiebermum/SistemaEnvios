using ECS.CommissionsMailer.Infrastructure.Firestore.Converters;
using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.Firestore.Models;

[FirestoreData]
public sealed class FirestoreAppliedDeductionSnapshot
{
    [FirestoreProperty("id", ConverterType = typeof(GuidStringConverter))]
    public Guid Id { get; set; }

    [FirestoreProperty("description")]
    public string Description { get; set; } = string.Empty;

    [FirestoreProperty("configuredAmount", ConverterType = typeof(DecimalStringConverter))]
    public decimal ConfiguredAmount { get; set; }

    [FirestoreProperty("appliedAmount", ConverterType = typeof(DecimalStringConverter))]
    public decimal AppliedAmount { get; set; }

    [FirestoreProperty("currency", ConverterType = typeof(FirestoreEnumNameConverter<FirestoreDeductionCurrency>))]
    public FirestoreDeductionCurrency Currency { get; set; }

    [FirestoreProperty("applicationType", ConverterType = typeof(FirestoreEnumNameConverter<FirestoreDeductionApplicationType>))]
    public FirestoreDeductionApplicationType ApplicationType { get; set; }

    [FirestoreProperty("targetWorksheetName")]
    public string TargetWorksheetName { get; set; } = string.Empty;

    [FirestoreProperty("displayOrder")]
    public int DisplayOrder { get; set; }
}

[FirestoreData]
public sealed class FirestorePaymentCurrencyCalculation
{
    [FirestoreProperty("currency", ConverterType = typeof(FirestoreEnumNameConverter<FirestoreDeductionCurrency>))]
    public FirestoreDeductionCurrency Currency { get; set; }

    [FirestoreProperty("hasCommission")]
    public bool HasCommission { get; set; }

    [FirestoreProperty("minimumApplied")]
    public bool MinimumApplied { get; set; }

    [FirestoreProperty("minimumAmount", ConverterType = typeof(DecimalStringConverter))]
    public decimal MinimumAmount { get; set; }

    [FirestoreProperty("grossCommissionOriginal", ConverterType = typeof(DecimalStringConverter))]
    public decimal GrossCommissionOriginal { get; set; }

    [FirestoreProperty("grossDeductions", ConverterType = typeof(DecimalStringConverter))]
    public decimal GrossDeductions { get; set; }

    [FirestoreProperty("adjustedGrossCommission", ConverterType = typeof(DecimalStringConverter))]
    public decimal AdjustedGrossCommission { get; set; }

    [FirestoreProperty("vat", ConverterType = typeof(DecimalStringConverter))]
    public decimal Vat { get; set; }

    [FirestoreProperty("invoiceAmount", ConverterType = typeof(DecimalStringConverter))]
    public decimal InvoiceAmount { get; set; }

    [FirestoreProperty("withholding", ConverterType = typeof(DecimalStringConverter))]
    public decimal Withholding { get; set; }

    [FirestoreProperty("payableBeforeFinalDeductions", ConverterType = typeof(DecimalStringConverter))]
    public decimal PayableBeforeFinalDeductions { get; set; }

    [FirestoreProperty("finalDeductions", ConverterType = typeof(DecimalStringConverter))]
    public decimal FinalDeductions { get; set; }

    [FirestoreProperty("depositedAmount", ConverterType = typeof(DecimalStringConverter))]
    public decimal DepositedAmount { get; set; }

    [FirestoreProperty("observation")]
    public string Observation { get; set; } = string.Empty;

    [FirestoreProperty("deductions")]
    public List<FirestoreAppliedDeductionSnapshot> Deductions { get; set; } = [];

    [FirestoreProperty("warnings")]
    public List<string> Warnings { get; set; } = [];

    [FirestoreProperty("errors")]
    public List<string> Errors { get; set; } = [];
}

[FirestoreData]
public sealed class FirestorePaymentGenerationDocument
{
    [FirestoreProperty("id", ConverterType = typeof(GuidStringConverter))]
    public Guid Id { get; set; }

    [FirestoreProperty("period")]
    public string Period { get; set; } = string.Empty;

    [FirestoreProperty("createdAtUtc", ConverterType = typeof(FirestoreTimestampPrecisionConverter))]
    public DateTimeOffset CreatedAtUtc { get; set; }

    [FirestoreProperty("sourceWorkbookSha256")]
    public string SourceWorkbookSha256 { get; set; } = string.Empty;

    [FirestoreProperty("status", ConverterType = typeof(FirestoreEnumNameConverter<FirestorePaymentGenerationStatus>))]
    public FirestorePaymentGenerationStatus Status { get; set; }

    [FirestoreProperty("sentBrokerIds")]
    public List<string> SentBrokerIds { get; set; } = [];

    [FirestoreProperty("failedBrokerIds")]
    public List<string> FailedBrokerIds { get; set; } = [];

    [FirestoreProperty("warnings")]
    public List<string> Warnings { get; set; } = [];
}

[FirestoreData]
public sealed class FirestorePaymentGenerationFileDocument
{
    [FirestoreProperty("id", ConverterType = typeof(GuidStringConverter))]
    public Guid Id { get; set; }

    [FirestoreProperty("brokerId", ConverterType = typeof(GuidStringConverter))]
    public Guid BrokerId { get; set; }

    [FirestoreProperty("brokerName")]
    public string BrokerName { get; set; } = string.Empty;

    [FirestoreProperty("worksheetName")]
    public string WorksheetName { get; set; } = string.Empty;

    [FirestoreProperty("sha256")]
    public string Sha256 { get; set; } = string.Empty;

    [FirestoreProperty("analyzerName")]
    public string AnalyzerName { get; set; } = string.Empty;

    [FirestoreProperty("generatedAtUtc", ConverterType = typeof(FirestoreTimestampPrecisionConverter))]
    public DateTimeOffset GeneratedAtUtc { get; set; }

    [FirestoreProperty("crc")]
    public FirestorePaymentCurrencyCalculation Crc { get; set; } = new()
    {
        Currency = FirestoreDeductionCurrency.CRC
    };

    [FirestoreProperty("usd")]
    public FirestorePaymentCurrencyCalculation Usd { get; set; } = new()
    {
        Currency = FirestoreDeductionCurrency.USD
    };
}
