namespace ECS.CommissionsMailer.Models.Expirations;

public sealed class ExpirationsBrokerConfigurationItem
{
    public Guid BrokerId { get; init; }
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> PrimaryEmailAddresses { get; init; } = [];
    public bool IsActive { get; init; } = true;
    public ExpirationsNextMonthGenerationMode NextMonthGenerationMode { get; init; }
    public IReadOnlyList<ExpirationsAssistant> Assistants { get; init; } = [];
    public IReadOnlyList<ExpirationsAssistant> CancellationAssistants { get; init; } = [];
    public ExpirationsProcess AssistantSummaryProcess { get; set; } = ExpirationsProcess.PreviousMonth;
    public bool HasExplicitProfile { get; init; }
    public string? ProfileUpdateTime { get; init; }
    public DateTimeOffset? ProfileCreatedAtUtc { get; init; }
    public DateTimeOffset? ProfileUpdatedAtUtc { get; init; }

    public string PrimaryEmailsText => HasPrimaryEmail
        ? string.Join("; ", PrimaryEmailAddresses.Where(value => !string.IsNullOrWhiteSpace(value)))
        : "(sin correo principal)";
    public string StatusText => IsActive ? "Activo" : "Inactivo";
    public int ActiveAssistantCount => SummaryAssistants.Count(assistant => assistant.IsActive);
    public int TotalAssistantCount => SummaryAssistants.Count;
    public bool HasPrimaryEmail => PrimaryEmailAddresses.Any(value => !string.IsNullOrWhiteSpace(value));

    private IReadOnlyList<ExpirationsAssistant> SummaryAssistants =>
        AssistantSummaryProcess == ExpirationsProcess.Cancellations
            ? CancellationAssistants
            : Assistants;
}

public sealed record ExpirationsNextMonthGenerationModeOption(
    ExpirationsNextMonthGenerationMode Value,
    string DisplayName);

public enum ExpirationsBrokerConfigurationSaveOutcome
{
    NoChanges,
    Created,
    Updated,
    ValidationFailed,
    ConcurrencyConflict
}

public sealed class ExpirationsBrokerConfigurationSaveResult
{
    public ExpirationsBrokerConfigurationSaveOutcome Outcome { get; init; }
    public ExpirationsBrokerConfigurationItem Configuration { get; init; } = new();
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string Message { get; init; } = string.Empty;
    public bool WasPersisted => Outcome is ExpirationsBrokerConfigurationSaveOutcome.Created or
        ExpirationsBrokerConfigurationSaveOutcome.Updated;
}

public sealed class ExpirationsAssistantValidationResult
{
    public IReadOnlyList<ExpirationsAssistant> Assistants { get; init; } = [];
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool IsValid => Errors.Count == 0;
}

public sealed class ExpirationsAssistantEditResult
{
    public ExpirationsAssistant? Assistant { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public bool IsValid => Assistant is not null && Errors.Count == 0;
}
