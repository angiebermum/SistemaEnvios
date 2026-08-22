namespace ECS.CommissionsMailer.Models.Expirations;

public enum ExpirationsSendOperationStatus
{
    InProgress,
    Completed
}

public enum ExpirationsSendItemStatus
{
    Pending,
    Succeeded,
    Failed
}

public sealed record ExpirationsSendAttachment(
    string FileName,
    string Sha256,
    ExpirationsGeneratedFileVariant Variant);

public sealed class ExpirationsSendOperation
{
    public Guid OperationId { get; init; }
    public ExpirationsProcess Process { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public string SendingAccountEmail { get; init; } = string.Empty;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public ExpirationsSendOperationStatus Status { get; init; }
    public int TotalCount { get; init; }
    public int SuccessCount { get; init; }
    public int FailureCount { get; init; }
    public Guid? RetryOfOperationId { get; init; }
}

public sealed class ExpirationsSendHistoryItem
{
    public Guid ItemId { get; init; }
    public Guid RequestId { get; init; }
    public Guid BrokerId { get; init; }
    public string BrokerName { get; init; } = string.Empty;
    public IReadOnlyList<string> ToRecipients { get; init; } = [];
    public IReadOnlyList<string> CcRecipients { get; init; } = [];
    public IReadOnlyList<ExpirationsSendAttachment> Attachments { get; init; } = [];
    public ExpirationsSendItemStatus Status { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
    public Guid? RetryOfItemId { get; init; }
}

public sealed record ExpirationsPreparedAttachment(
    string Path,
    string FileName,
    string Sha256,
    ExpirationsGeneratedFileVariant Variant);

public sealed class ExpirationsPreparedSendItem
{
    public Guid RequestId { get; init; }
    public Guid? RetryOfItemId { get; init; }
    public IReadOnlyList<ExpirationsPreparedAttachment> Attachments { get; init; } = [];
}

public sealed class ExpirationsSendHistoryEntry
{
    public required ExpirationsSendOperation Operation { get; init; }
    public IReadOnlyList<ExpirationsSendHistoryItem> Items { get; init; } = [];
}

public sealed class ExpirationsRetryPreparationResult
{
    public ExpirationsSendPreparationResult? Preparation { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool CanRetry => Errors.Count == 0 && Preparation?.CanSend == true;
}
