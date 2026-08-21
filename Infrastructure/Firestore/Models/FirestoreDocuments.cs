using ECS.CommissionsMailer.Infrastructure.Firestore.Converters;
using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.Firestore.Models;

[FirestoreData]
public sealed class FirestoreCommissionsSettingsDocument
{
    [FirestoreProperty("defaultSubject")]
    public string DefaultSubject { get; set; } = string.Empty;

    [FirestoreProperty("defaultMessage")]
    public string DefaultMessage { get; set; } = string.Empty;

    [FirestoreProperty("dataSchemaVersion")]
    public int DataSchemaVersion { get; set; }

    [FirestoreProperty("emailDirectorySeedVersion")]
    public int EmailDirectorySeedVersion { get; set; }

    [FirestoreProperty("emailDirectorySeedId")]
    public string? EmailDirectorySeedId { get; set; }

    [FirestoreProperty("commonCcAddresses")]
    public List<string> CommonCcAddresses { get; set; } = [];
}

[FirestoreData]
public sealed class FirestoreBrokerAssistant
{
    [FirestoreProperty("id", ConverterType = typeof(GuidStringConverter))]
    public Guid Id { get; set; }

    [FirestoreProperty("name")]
    public string Name { get; set; } = string.Empty;

    [FirestoreProperty("email")]
    public string Email { get; set; } = string.Empty;

    [FirestoreProperty("isActive")]
    public bool IsActive { get; set; }
}

[FirestoreData]
public sealed class FirestoreBrokerDeduction
{
    [FirestoreProperty("id", ConverterType = typeof(GuidStringConverter))]
    public Guid Id { get; set; }

    [FirestoreProperty("description")]
    public string Description { get; set; } = string.Empty;

    [FirestoreProperty("amount", ConverterType = typeof(DecimalStringConverter))]
    public decimal Amount { get; set; }

    [FirestoreProperty("currency", ConverterType = typeof(FirestoreEnumNameConverter<FirestoreDeductionCurrency>))]
    public FirestoreDeductionCurrency Currency { get; set; }

    [FirestoreProperty("applicationType", ConverterType = typeof(FirestoreEnumNameConverter<FirestoreDeductionApplicationType>))]
    public FirestoreDeductionApplicationType ApplicationType { get; set; }

    [FirestoreProperty("targetWorksheetName")]
    public string? TargetWorksheetName { get; set; }

    [FirestoreProperty("displayOrder")]
    public int DisplayOrder { get; set; }
}

[FirestoreData]
public sealed class FirestoreBrokerDocument
{
    [FirestoreProperty("id", ConverterType = typeof(GuidStringConverter))]
    public Guid Id { get; set; }

    [FirestoreProperty("seedKey")]
    public string? SeedKey { get; set; }

    [FirestoreProperty("name")]
    public string Name { get; set; } = string.Empty;

    [FirestoreProperty("primaryEmailAddresses")]
    public List<string> PrimaryEmailAddresses { get; set; } = [];

    [FirestoreProperty("assistants")]
    public List<FirestoreBrokerAssistant> Assistants { get; set; } = [];

    [FirestoreProperty("associatedWorksheetNames")]
    public List<string> AssociatedWorksheetNames { get; set; } = [];

    [FirestoreProperty("deductions")]
    public List<FirestoreBrokerDeduction> Deductions { get; set; } = [];

    [FirestoreProperty("isActive")]
    public bool IsActive { get; set; }

    [FirestoreProperty("requiresReview")]
    public bool RequiresReview { get; set; }

    [FirestoreProperty("reviewNote")]
    public string? ReviewNote { get; set; }
}

[FirestoreData]
public sealed class FirestoreCurrentSessionDocument
{
    [FirestoreProperty("subject")]
    public string Subject { get; set; } = string.Empty;

    [FirestoreProperty("message")]
    public string Message { get; set; } = string.Empty;

    [FirestoreProperty("commonCcText")]
    public string CommonCcText { get; set; } = string.Empty;

    [FirestoreProperty("activePaymentGenerationId", ConverterType = typeof(GuidStringConverter))]
    public Guid? ActivePaymentGenerationId { get; set; }

    [FirestoreProperty("generatedPeriod")]
    public string GeneratedPeriod { get; set; } = string.Empty;

    [FirestoreProperty("savedAtUtc", ConverterType = typeof(FirestoreTimestampPrecisionConverter))]
    public DateTimeOffset SavedAtUtc { get; set; }
}

[FirestoreData]
public sealed class FirestoreBrokerSendItemDocument
{
    [FirestoreProperty("brokerId", ConverterType = typeof(GuidStringConverter))]
    public Guid BrokerId { get; set; }

    [FirestoreProperty("brokerName")]
    public string BrokerName { get; set; } = string.Empty;

    [FirestoreProperty("seedKey")]
    public string? SeedKey { get; set; }

    [FirestoreProperty("primaryRecipients")]
    public List<string> PrimaryRecipients { get; set; } = [];

    [FirestoreProperty("assistants")]
    public List<FirestoreBrokerAssistant> Assistants { get; set; } = [];

    [FirestoreProperty("requiresReview")]
    public bool RequiresReview { get; set; }

    [FirestoreProperty("reviewNote")]
    public string ReviewNote { get; set; } = string.Empty;

    [FirestoreProperty("requiresBatchReview")]
    public bool RequiresBatchReview { get; set; }

    [FirestoreProperty("batchReviewNote")]
    public string BatchReviewNote { get; set; } = string.Empty;

    [FirestoreProperty("isSelected")]
    public bool IsSelected { get; set; }

    [FirestoreProperty("status", ConverterType = typeof(FirestoreEnumNameConverter<FirestoreSendStatus>))]
    public FirestoreSendStatus Status { get; set; }

    [FirestoreProperty("lastError")]
    public string LastError { get; set; } = string.Empty;
}

[FirestoreData]
public sealed class FirestoreRecentSendDocument
{
    [FirestoreProperty("id", ConverterType = typeof(GuidStringConverter))]
    public Guid Id { get; set; }

    [FirestoreProperty("brokerId", ConverterType = typeof(GuidStringConverter))]
    public Guid BrokerId { get; set; }

    [FirestoreProperty("brokerName")]
    public string BrokerName { get; set; } = string.Empty;

    [FirestoreProperty("brokerPrimaryRecipients")]
    public List<string> BrokerPrimaryRecipients { get; set; } = [];

    [FirestoreProperty("assistantRecipients")]
    public List<string> AssistantRecipients { get; set; } = [];

    [FirestoreProperty("toRecipients")]
    public List<string> ToRecipients { get; set; } = [];

    [FirestoreProperty("ccRecipients")]
    public List<string> CcRecipients { get; set; } = [];

    [FirestoreProperty("subject")]
    public string Subject { get; set; } = string.Empty;

    [FirestoreProperty("body")]
    public string Body { get; set; } = string.Empty;

    [FirestoreProperty("sentAtUtc", ConverterType = typeof(FirestoreTimestampPrecisionConverter))]
    public DateTimeOffset SentAtUtc { get; set; }

    [FirestoreProperty("wasSuccessful")]
    public bool WasSuccessful { get; set; }

    [FirestoreProperty("errorMessage")]
    public string ErrorMessage { get; set; } = string.Empty;

    [FirestoreProperty("resendOfRecordId", ConverterType = typeof(GuidStringConverter))]
    public Guid? ResendOfRecordId { get; set; }

    [FirestoreProperty("paymentGenerationId", ConverterType = typeof(GuidStringConverter))]
    public Guid? PaymentGenerationId { get; set; }
}
