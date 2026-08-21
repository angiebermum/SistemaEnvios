using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

internal static class FirestoreDomainFields
{
    public static FirestoreRestValue Strings(IEnumerable<string>? values) =>
        FirestoreRestValue.Array((values ?? []).Select(FirestoreRestValue.String));

    public static List<string> ReadStrings(FirestoreRestValue value, string name) =>
        value.RequireArray(name).Select((item, index) => item.RequireString($"{name}[{index}]")).ToList();

    public static FirestoreRestValue Guid(Guid value) => FirestoreRestValue.String(value.ToString("D"));
    public static FirestoreRestValue NullableGuid(Guid? value) => value.HasValue ? Guid(value.Value) : FirestoreRestValue.Null();
    public static Guid ReadGuid(FirestoreRestValue value, string name) =>
        System.Guid.TryParse(value.RequireString(name), out var parsed)
            ? parsed
            : throw new InvalidDataException($"El campo '{name}' no contiene un UUID válido.");
    public static Guid? ReadNullableGuid(FirestoreRestValue value, string name) =>
        value.IsNull ? null : ReadGuid(value, name);
    public static FirestoreRestValue EnumName<T>(T value) where T : struct, Enum =>
        FirestoreRestValue.String(value.ToString());
    public static T ReadEnum<T>(FirestoreRestValue value, string name) where T : struct, Enum =>
        Enum.TryParse<T>(value.RequireString(name), out var parsed)
            ? parsed
            : throw new InvalidDataException($"El campo '{name}' contiene un enum no permitido.");
}

internal sealed class FirestoreSettingsMapper : IFirestoreEntityMapper<AppConfiguration>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(AppConfiguration value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["defaultSubject"] = FirestoreRestValue.String(value.DefaultSubject),
            ["defaultMessage"] = FirestoreRestValue.String(value.DefaultMessage),
            ["dataSchemaVersion"] = FirestoreRestValue.Integer(value.DataSchemaVersion),
            ["emailDirectorySeedVersion"] = FirestoreRestValue.Integer(value.EmailDirectorySeedVersion),
            ["emailDirectorySeedId"] = FirestoreRestValue.String(value.EmailDirectorySeedId),
            ["commonCcAddresses"] = FirestoreDomainFields.Strings(value.CommonCcAddresses)
        };

    public AppConfiguration FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        DefaultSubject = fields.Required("defaultSubject").RequireString("defaultSubject"),
        DefaultMessage = fields.Required("defaultMessage").RequireString("defaultMessage"),
        DataSchemaVersion = checked((int)fields.Required("dataSchemaVersion").RequireInteger("dataSchemaVersion")),
        EmailDirectorySeedVersion = checked((int)fields.Required("emailDirectorySeedVersion").RequireInteger("emailDirectorySeedVersion")),
        EmailDirectorySeedId = fields.Required("emailDirectorySeedId").OptionalString("emailDirectorySeedId"),
        CommonCcAddresses = FirestoreDomainFields.ReadStrings(fields.Required("commonCcAddresses"), "commonCcAddresses")
    };
}

internal sealed class FirestoreBrokerMapper : IFirestoreEntityMapper<Broker>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(Broker value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreDomainFields.Guid(value.Id),
            ["seedKey"] = FirestoreRestValue.String(value.SeedKey),
            ["name"] = FirestoreRestValue.String(value.Name),
            ["primaryEmailAddresses"] = FirestoreDomainFields.Strings(value.PrimaryEmailAddresses),
            ["assistants"] = FirestoreRestValue.Array(value.Assistants.Select(Assistant)),
            ["associatedWorksheetNames"] = FirestoreDomainFields.Strings(value.AssociatedWorksheetNames),
            ["deductions"] = FirestoreRestValue.Array(value.Deductions.Select(Deduction)),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive),
            ["requiresReview"] = FirestoreRestValue.Boolean(value.RequiresReview),
            ["reviewNote"] = FirestoreRestValue.String(value.ReviewNote)
        };

    public Broker FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        Id = FirestoreDomainFields.ReadGuid(fields.Required("id"), "id"),
        SeedKey = fields.Required("seedKey").OptionalString("seedKey"),
        Name = fields.Required("name").RequireString("name"),
        PrimaryEmailAddresses = FirestoreDomainFields.ReadStrings(fields.Required("primaryEmailAddresses"), "primaryEmailAddresses"),
        Assistants = fields.Required("assistants").RequireArray("assistants").Select(ReadAssistant).ToList(),
        AssociatedWorksheetNames = FirestoreDomainFields.ReadStrings(fields.Required("associatedWorksheetNames"), "associatedWorksheetNames"),
        Deductions = fields.Required("deductions").RequireArray("deductions").Select(ReadDeduction).ToList(),
        IsActive = fields.Required("isActive").RequireBoolean("isActive"),
        RequiresReview = fields.Required("requiresReview").RequireBoolean("requiresReview"),
        ReviewNote = fields.Required("reviewNote").OptionalString("reviewNote")
    };

    internal static FirestoreRestValue Assistant(BrokerAssistant value) => FirestoreRestValue.Map(
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreDomainFields.Guid(value.Id),
            ["name"] = FirestoreRestValue.String(value.Name),
            ["email"] = FirestoreRestValue.String(value.Email),
            ["isActive"] = FirestoreRestValue.Boolean(value.IsActive)
        });

    internal static BrokerAssistant ReadAssistant(FirestoreRestValue value)
    {
        var fields = value.RequireMap("assistant");
        return new BrokerAssistant
        {
            Id = FirestoreDomainFields.ReadGuid(fields.Required("id"), "assistant.id"),
            Name = fields.Required("name").RequireString("assistant.name"),
            Email = fields.Required("email").RequireString("assistant.email"),
            IsActive = fields.Required("isActive").RequireBoolean("assistant.isActive")
        };
    }

    private static FirestoreRestValue Deduction(BrokerDeduction value) => FirestoreRestValue.Map(
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreDomainFields.Guid(value.Id),
            ["description"] = FirestoreRestValue.String(value.Description),
            ["amount"] = FirestoreRestValue.Decimal(value.Amount),
            ["currency"] = FirestoreDomainFields.EnumName(value.Currency),
            ["applicationType"] = FirestoreDomainFields.EnumName(value.ApplicationType),
            ["targetWorksheetName"] = FirestoreRestValue.String(value.TargetWorksheetName),
            ["displayOrder"] = FirestoreRestValue.Integer(value.DisplayOrder)
        });

    private static BrokerDeduction ReadDeduction(FirestoreRestValue value)
    {
        var fields = value.RequireMap("deduction");
        return new BrokerDeduction
        {
            Id = FirestoreDomainFields.ReadGuid(fields.Required("id"), "deduction.id"),
            Description = fields.Required("description").RequireString("deduction.description"),
            Amount = fields.Required("amount").RequireDecimal("deduction.amount"),
            Currency = FirestoreDomainFields.ReadEnum<DeductionCurrency>(fields.Required("currency"), "deduction.currency"),
            ApplicationType = FirestoreDomainFields.ReadEnum<DeductionApplicationType>(fields.Required("applicationType"), "deduction.applicationType"),
            TargetWorksheetName = fields.Required("targetWorksheetName").OptionalString("deduction.targetWorksheetName"),
            DisplayOrder = checked((int)fields.Required("displayOrder").RequireInteger("deduction.displayOrder"))
        };
    }
}

internal sealed class FirestoreCurrentSessionMapper : IFirestoreEntityMapper<CurrentSession>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(CurrentSession value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["subject"] = FirestoreRestValue.String(value.Subject),
            ["message"] = FirestoreRestValue.String(value.Message),
            ["commonCcText"] = FirestoreRestValue.String(value.CommonCcText),
            ["activePaymentGenerationId"] = FirestoreDomainFields.NullableGuid(value.ActivePaymentGenerationId),
            ["generatedPeriod"] = FirestoreRestValue.String(value.GeneratedPeriod),
            ["savedAtUtc"] = FirestoreRestValue.Timestamp(value.SavedAt)
        };

    public CurrentSession FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        Subject = fields.Required("subject").RequireString("subject"),
        Message = fields.Required("message").RequireString("message"),
        CommonCcText = fields.Required("commonCcText").RequireString("commonCcText"),
        ActivePaymentGenerationId = FirestoreDomainFields.ReadNullableGuid(fields.Required("activePaymentGenerationId"), "activePaymentGenerationId"),
        GeneratedPeriod = fields.Required("generatedPeriod").RequireString("generatedPeriod"),
        SavedAt = fields.Required("savedAtUtc").RequireTimestamp("savedAtUtc")
    };
}

internal sealed class FirestoreBrokerSendItemMapper : IFirestoreEntityMapper<BrokerSendItem>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(BrokerSendItem value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["brokerId"] = FirestoreDomainFields.Guid(value.BrokerId),
            ["brokerName"] = FirestoreRestValue.String(value.BrokerName),
            ["seedKey"] = FirestoreRestValue.String(value.SeedKey),
            ["primaryRecipients"] = FirestoreDomainFields.Strings(value.PrimaryRecipients),
            ["assistants"] = FirestoreRestValue.Array(value.Assistants.Select(FirestoreBrokerMapper.Assistant)),
            ["requiresReview"] = FirestoreRestValue.Boolean(value.RequiresReview),
            ["reviewNote"] = FirestoreRestValue.String(value.ReviewNote),
            ["requiresBatchReview"] = FirestoreRestValue.Boolean(value.RequiresBatchReview),
            ["batchReviewNote"] = FirestoreRestValue.String(value.BatchReviewNote),
            ["isSelected"] = FirestoreRestValue.Boolean(value.IsSelected),
            ["status"] = FirestoreDomainFields.EnumName(value.Status),
            ["lastError"] = FirestoreRestValue.String(value.LastError)
        };

    public BrokerSendItem FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        BrokerId = FirestoreDomainFields.ReadGuid(fields.Required("brokerId"), "brokerId"),
        BrokerName = fields.Required("brokerName").RequireString("brokerName"),
        SeedKey = fields.Required("seedKey").OptionalString("seedKey"),
        PrimaryRecipients = FirestoreDomainFields.ReadStrings(fields.Required("primaryRecipients"), "primaryRecipients"),
        Assistants = fields.Required("assistants").RequireArray("assistants").Select(FirestoreBrokerMapper.ReadAssistant).ToList(),
        RequiresReview = fields.Required("requiresReview").RequireBoolean("requiresReview"),
        ReviewNote = fields.Required("reviewNote").OptionalString("reviewNote") ?? string.Empty,
        RequiresBatchReview = fields.Required("requiresBatchReview").RequireBoolean("requiresBatchReview"),
        BatchReviewNote = fields.Required("batchReviewNote").OptionalString("batchReviewNote") ?? string.Empty,
        IsSelected = fields.Required("isSelected").RequireBoolean("isSelected"),
        Status = FirestoreDomainFields.ReadEnum<SendStatus>(fields.Required("status"), "status"),
        LastError = fields.Required("lastError").OptionalString("lastError") ?? string.Empty
    };
}

internal sealed class FirestoreRecentSendMapper : IFirestoreEntityMapper<SentEmailRecord>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(SentEmailRecord value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreDomainFields.Guid(value.Id),
            ["brokerId"] = FirestoreDomainFields.Guid(value.BrokerId),
            ["brokerName"] = FirestoreRestValue.String(value.BrokerName),
            ["brokerPrimaryRecipients"] = FirestoreDomainFields.Strings(value.BrokerPrimaryRecipients),
            ["assistantRecipients"] = FirestoreDomainFields.Strings(value.AssistantRecipients),
            ["toRecipients"] = FirestoreDomainFields.Strings(value.ToRecipients),
            ["ccRecipients"] = FirestoreDomainFields.Strings(value.CcRecipients),
            ["subject"] = FirestoreRestValue.String(value.Subject),
            ["body"] = FirestoreRestValue.String(value.Body),
            ["sentAtUtc"] = FirestoreRestValue.Timestamp(value.SentAt),
            ["wasSuccessful"] = FirestoreRestValue.Boolean(value.WasSuccessful),
            ["errorMessage"] = FirestoreRestValue.String(value.ErrorMessage),
            ["resendOfRecordId"] = FirestoreDomainFields.NullableGuid(value.ResendOfRecordId),
            ["paymentGenerationId"] = FirestoreDomainFields.NullableGuid(value.PaymentGenerationId)
        };

    public SentEmailRecord FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        Id = FirestoreDomainFields.ReadGuid(fields.Required("id"), "id"),
        BrokerId = FirestoreDomainFields.ReadGuid(fields.Required("brokerId"), "brokerId"),
        BrokerName = fields.Required("brokerName").RequireString("brokerName"),
        BrokerPrimaryRecipients = FirestoreDomainFields.ReadStrings(fields.Required("brokerPrimaryRecipients"), "brokerPrimaryRecipients"),
        AssistantRecipients = FirestoreDomainFields.ReadStrings(fields.Required("assistantRecipients"), "assistantRecipients"),
        ToRecipients = FirestoreDomainFields.ReadStrings(fields.Required("toRecipients"), "toRecipients"),
        CcRecipients = FirestoreDomainFields.ReadStrings(fields.Required("ccRecipients"), "ccRecipients"),
        Subject = fields.Required("subject").RequireString("subject"),
        Body = fields.Required("body").RequireString("body"),
        SentAt = fields.Required("sentAtUtc").RequireTimestamp("sentAtUtc"),
        WasSuccessful = fields.Required("wasSuccessful").RequireBoolean("wasSuccessful"),
        ErrorMessage = fields.Required("errorMessage").OptionalString("errorMessage") ?? string.Empty,
        ResendOfRecordId = FirestoreDomainFields.ReadNullableGuid(fields.Required("resendOfRecordId"), "resendOfRecordId"),
        PaymentGenerationId = FirestoreDomainFields.ReadNullableGuid(fields.Required("paymentGenerationId"), "paymentGenerationId")
    };
}

internal sealed class FirestorePaymentGenerationMapper : IFirestoreEntityMapper<PaymentGenerationBatch>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(PaymentGenerationBatch value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreDomainFields.Guid(value.Id),
            ["period"] = FirestoreRestValue.String(value.Period),
            ["createdAtUtc"] = FirestoreRestValue.Timestamp(value.CreatedAt),
            ["sourceWorkbookSha256"] = FirestoreRestValue.String(value.SourceWorkbookSha256),
            ["status"] = FirestoreDomainFields.EnumName(value.Status),
            ["sentBrokerIds"] = FirestoreDomainFields.Strings(value.SentBrokerIds.Select(id => id.ToString("D"))),
            ["failedBrokerIds"] = FirestoreDomainFields.Strings(value.FailedBrokerIds.Select(id => id.ToString("D"))),
            ["warnings"] = FirestoreDomainFields.Strings(value.Warnings)
        };

    public PaymentGenerationBatch FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new()
    {
        Id = FirestoreDomainFields.ReadGuid(fields.Required("id"), "id"),
        Period = fields.Required("period").RequireString("period"),
        CreatedAt = fields.Required("createdAtUtc").RequireTimestamp("createdAtUtc"),
        SourceWorkbookSha256 = fields.Required("sourceWorkbookSha256").RequireString("sourceWorkbookSha256"),
        Status = FirestoreDomainFields.ReadEnum<PaymentGenerationStatus>(fields.Required("status"), "status"),
        SentBrokerIds = FirestoreDomainFields.ReadStrings(fields.Required("sentBrokerIds"), "sentBrokerIds").Select(Guid.Parse).ToList(),
        FailedBrokerIds = FirestoreDomainFields.ReadStrings(fields.Required("failedBrokerIds"), "failedBrokerIds").Select(Guid.Parse).ToList(),
        Warnings = FirestoreDomainFields.ReadStrings(fields.Required("warnings"), "warnings")
    };
}

internal sealed record FirestoreGenerationFile(Guid Id, GeneratedPaymentFile Value);

internal sealed class FirestorePaymentGenerationFileMapper : IFirestoreEntityMapper<FirestoreGenerationFile>
{
    public IReadOnlyDictionary<string, FirestoreRestValue> ToFields(FirestoreGenerationFile value) =>
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreDomainFields.Guid(value.Id),
            ["brokerId"] = FirestoreDomainFields.Guid(value.Value.BrokerId),
            ["brokerName"] = FirestoreRestValue.String(value.Value.BrokerName),
            ["worksheetName"] = FirestoreRestValue.String(value.Value.WorksheetName),
            ["sha256"] = FirestoreRestValue.String(value.Value.Sha256),
            ["analyzerName"] = FirestoreRestValue.String(value.Value.AnalyzerName),
            ["generatedAtUtc"] = FirestoreRestValue.Timestamp(value.Value.GeneratedAt),
            ["crc"] = Currency(value.Value.Crc),
            ["usd"] = Currency(value.Value.Usd)
        };

    public FirestoreGenerationFile FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields) => new(
        FirestoreDomainFields.ReadGuid(fields.Required("id"), "id"),
        new GeneratedPaymentFile
        {
            BrokerId = FirestoreDomainFields.ReadGuid(fields.Required("brokerId"), "brokerId"),
            BrokerName = fields.Required("brokerName").RequireString("brokerName"),
            WorksheetName = fields.Required("worksheetName").RequireString("worksheetName"),
            Sha256 = fields.Required("sha256").RequireString("sha256"),
            AnalyzerName = fields.Required("analyzerName").RequireString("analyzerName"),
            GeneratedAt = fields.Required("generatedAtUtc").RequireTimestamp("generatedAtUtc"),
            Crc = ReadCurrency(fields.Required("crc")),
            Usd = ReadCurrency(fields.Required("usd"))
        });

    private static FirestoreRestValue Currency(PaymentCurrencyCalculation value) => FirestoreRestValue.Map(
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["currency"] = FirestoreDomainFields.EnumName(value.Currency),
            ["hasCommission"] = FirestoreRestValue.Boolean(value.HasCommission),
            ["minimumApplied"] = FirestoreRestValue.Boolean(value.MinimumApplied),
            ["minimumAmount"] = FirestoreRestValue.Decimal(value.MinimumAmount),
            ["grossCommissionOriginal"] = FirestoreRestValue.Decimal(value.GrossCommissionOriginal),
            ["grossDeductions"] = FirestoreRestValue.Decimal(value.GrossDeductions),
            ["adjustedGrossCommission"] = FirestoreRestValue.Decimal(value.AdjustedGrossCommission),
            ["vat"] = FirestoreRestValue.Decimal(value.Vat),
            ["invoiceAmount"] = FirestoreRestValue.Decimal(value.InvoiceAmount),
            ["withholding"] = FirestoreRestValue.Decimal(value.Withholding),
            ["payableBeforeFinalDeductions"] = FirestoreRestValue.Decimal(value.PayableBeforeFinalDeductions),
            ["finalDeductions"] = FirestoreRestValue.Decimal(value.FinalDeductions),
            ["depositedAmount"] = FirestoreRestValue.Decimal(value.DepositedAmount),
            ["observation"] = FirestoreRestValue.String(value.Observation),
            ["deductions"] = FirestoreRestValue.Array(value.Deductions.Select(Deduction)),
            ["warnings"] = FirestoreDomainFields.Strings(value.Warnings),
            ["errors"] = FirestoreDomainFields.Strings(value.Errors)
        });

    private static PaymentCurrencyCalculation ReadCurrency(FirestoreRestValue value)
    {
        var fields = value.RequireMap("currencySnapshot");
        return new PaymentCurrencyCalculation
        {
            Currency = FirestoreDomainFields.ReadEnum<DeductionCurrency>(fields.Required("currency"), "currency"),
            HasCommission = fields.Required("hasCommission").RequireBoolean("hasCommission"),
            MinimumApplied = fields.Required("minimumApplied").RequireBoolean("minimumApplied"),
            MinimumAmount = fields.Required("minimumAmount").RequireDecimal("minimumAmount"),
            GrossCommissionOriginal = fields.Required("grossCommissionOriginal").RequireDecimal("grossCommissionOriginal"),
            GrossDeductions = fields.Required("grossDeductions").RequireDecimal("grossDeductions"),
            AdjustedGrossCommission = fields.Required("adjustedGrossCommission").RequireDecimal("adjustedGrossCommission"),
            Vat = fields.Required("vat").RequireDecimal("vat"),
            InvoiceAmount = fields.Required("invoiceAmount").RequireDecimal("invoiceAmount"),
            Withholding = fields.Required("withholding").RequireDecimal("withholding"),
            PayableBeforeFinalDeductions = fields.Required("payableBeforeFinalDeductions").RequireDecimal("payableBeforeFinalDeductions"),
            FinalDeductions = fields.Required("finalDeductions").RequireDecimal("finalDeductions"),
            DepositedAmount = fields.Required("depositedAmount").RequireDecimal("depositedAmount"),
            Observation = fields.Required("observation").RequireString("observation"),
            Deductions = fields.Required("deductions").RequireArray("deductions").Select(ReadDeduction).ToList(),
            Warnings = FirestoreDomainFields.ReadStrings(fields.Required("warnings"), "warnings"),
            Errors = FirestoreDomainFields.ReadStrings(fields.Required("errors"), "errors")
        };
    }

    private static FirestoreRestValue Deduction(AppliedDeductionSnapshot value) => FirestoreRestValue.Map(
        new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["id"] = FirestoreDomainFields.Guid(value.Id),
            ["description"] = FirestoreRestValue.String(value.Description),
            ["configuredAmount"] = FirestoreRestValue.Decimal(value.ConfiguredAmount),
            ["appliedAmount"] = FirestoreRestValue.Decimal(value.AppliedAmount),
            ["currency"] = FirestoreDomainFields.EnumName(value.Currency),
            ["applicationType"] = FirestoreDomainFields.EnumName(value.ApplicationType),
            ["targetWorksheetName"] = FirestoreRestValue.String(value.TargetWorksheetName),
            ["displayOrder"] = FirestoreRestValue.Integer(value.DisplayOrder)
        });

    private static AppliedDeductionSnapshot ReadDeduction(FirestoreRestValue value)
    {
        var fields = value.RequireMap("appliedDeduction");
        return new AppliedDeductionSnapshot
        {
            Id = FirestoreDomainFields.ReadGuid(fields.Required("id"), "deduction.id"),
            Description = fields.Required("description").RequireString("deduction.description"),
            ConfiguredAmount = fields.Required("configuredAmount").RequireDecimal("deduction.configuredAmount"),
            AppliedAmount = fields.Required("appliedAmount").RequireDecimal("deduction.appliedAmount"),
            Currency = FirestoreDomainFields.ReadEnum<DeductionCurrency>(fields.Required("currency"), "deduction.currency"),
            ApplicationType = FirestoreDomainFields.ReadEnum<DeductionApplicationType>(fields.Required("applicationType"), "deduction.applicationType"),
            TargetWorksheetName = fields.Required("targetWorksheetName").RequireString("deduction.targetWorksheetName"),
            DisplayOrder = checked((int)fields.Required("displayOrder").RequireInteger("deduction.displayOrder"))
        };
    }
}

internal static class RuntimeDeterministicDocumentIds
{
    public static Guid ForGenerationFile(Guid generationId, GeneratedPaymentFile file)
    {
        var canonical = new CanonicalText("ecs-generation-file-v1")
            .Guid("generationId", generationId)
            .Guid("brokerId", file.BrokerId)
            .Text("worksheetName", file.WorksheetName)
            .Text("sha256", file.Sha256)
            .ToString();
        return Sha256Guid(canonical);
    }

    public static Guid ForRecentSend(SentEmailRecord send)
    {
        var canonical = new CanonicalText("ecs-recent-send-v1")
            .Guid("brokerId", send.BrokerId)
            .Text("brokerName", send.BrokerName)
            .Texts("brokerPrimaryRecipients", send.BrokerPrimaryRecipients)
            .Texts("assistantRecipients", send.AssistantRecipients)
            .Texts("toRecipients", send.ToRecipients)
            .Texts("ccRecipients", send.CcRecipients)
            .Text("subject", send.Subject)
            .Text("body", send.Body)
            .Text("sentAtUtc", ECS.CommissionsMailer.Infrastructure.Firestore.FirestoreTimestampPrecision.Normalize(send.SentAt).ToString("O", CultureInfo.InvariantCulture))
            .Text("wasSuccessful", send.WasSuccessful ? "true" : "false")
            .Text("errorMessage", send.ErrorMessage)
            .NullableGuid("resendOfRecordId", send.ResendOfRecordId)
            .NullableGuid("paymentGenerationId", send.PaymentGenerationId)
            .ToString();
        return Sha256Guid(canonical);
    }

    private static Guid Sha256Guid(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Guid.ParseExact(Convert.ToHexString(hash.AsSpan(0, 16)), "N");
    }

    private sealed class CanonicalText
    {
        private readonly StringBuilder _value = new();
        public CanonicalText(string format) => Append("format", format);
        public CanonicalText Guid(string name, Guid value) => Text(name, value.ToString("D"));
        public CanonicalText NullableGuid(string name, Guid? value) => Text(name, value?.ToString("D"));
        public CanonicalText Text(string name, string? value) { Append(name, value); return this; }
        public CanonicalText Texts(string name, IEnumerable<string>? values)
        {
            var list = (values ?? []).ToList();
            Append($"{name}.count", list.Count.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < list.Count; index++) Append($"{name}[{index}]", list[index]);
            return this;
        }
        public override string ToString() => _value.ToString();
        private void Append(string name, string? value)
        {
            var bytes = value is null ? -1 : Encoding.UTF8.GetByteCount(value);
            _value.Append(name).Append('=').Append(bytes.ToString(CultureInfo.InvariantCulture)).Append(':');
            if (value is not null) _value.Append(value);
            _value.Append('\n');
        }
    }
}
