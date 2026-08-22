using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Models.Expirations;

public sealed record ExpirationsEmailSettingsSnapshot(
    ExpirationsProcess Process,
    ExpirationsProcessSettings Settings,
    string? UpdateTime)
{
    public bool Exists => !string.IsNullOrWhiteSpace(UpdateTime);
}

public enum ExpirationsEmailSettingsSaveOutcome
{
    Created,
    Updated,
    ValidationFailed,
    ConcurrencyConflict
}

public sealed class ExpirationsEmailSettingsSaveResult
{
    public ExpirationsEmailSettingsSaveOutcome Outcome { get; init; }
    public ExpirationsEmailSettingsSnapshot Snapshot { get; init; } = new(
        ExpirationsProcess.PreviousMonth,
        new ExpirationsProcessSettings(),
        null);
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string Message { get; init; } = string.Empty;
    public bool WasPersisted => Outcome is
        ExpirationsEmailSettingsSaveOutcome.Created or
        ExpirationsEmailSettingsSaveOutcome.Updated;
}

public sealed class ExpirationsRecipientResolution
{
    public IReadOnlyList<string> BrokerPrimaryRecipients { get; init; } = [];
    public IReadOnlyList<string> AssistantRecipients { get; init; } = [];
    public IReadOnlyList<string> ToRecipients { get; init; } = [];
    public IReadOnlyList<string> CcRecipients { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool IsValid => Errors.Count == 0 && ToRecipients.Count > 0;
}

public sealed class ExpirationsSendPreparationResult
{
    public ExpirationsProcess Process { get; init; }
    public ExpirationsProcessSettings? Settings { get; init; }
    public IReadOnlyList<EmailSendRequest> Requests { get; init; } = [];
    public IReadOnlyList<ExpirationsPreparedSendItem> PreparedItems { get; init; } = [];
    public Guid? RetryOfOperationId { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool CanSend => Errors.Count == 0 && Requests.Count > 0;
}

public sealed record ExpirationsSendResultItem(
    Guid RequestId,
    Guid BrokerId,
    string BrokerName,
    bool WasSuccessful,
    string ErrorMessage,
    bool IsConfirmed = true)
{
    public string StatusText => !IsConfirmed
        ? $"? {BrokerName} — resultado no confirmado"
        : WasSuccessful
        ? $"✓ {BrokerName}"
        : $"✗ {BrokerName} — {ErrorMessage}";
}

public sealed class ExpirationsSendExecutionResult
{
    public bool WasCancelled { get; init; }
    public string SendingAccount { get; init; } = string.Empty;
    public Guid? OperationId { get; init; }
    public string HistoryWarning { get; init; } = string.Empty;
    public IReadOnlyList<ExpirationsSendResultItem> Items { get; init; } = [];
    public int SuccessfulCount => Items.Count(item => item.IsConfirmed && item.WasSuccessful);
    public int FailedCount => Items.Count(item => item.IsConfirmed && !item.WasSuccessful);
    public int UnknownCount => Items.Count(item => !item.IsConfirmed);
    public bool HistoryIsComplete => HistoryWarning.Length == 0;
}
