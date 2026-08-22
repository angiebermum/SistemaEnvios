namespace ECS.CommissionsMailer.Models.Expirations;

public sealed class ExpirationsBrokerDirectoryEntry
{
    public Guid BrokerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> PrimaryEmailAddresses { get; set; } = [];
}

public sealed class ExpirationsAssistant
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}

public sealed class ExpirationsBrokerProfile
{
    public Guid BrokerId { get; set; }
    public bool IsActive { get; set; }
    public ExpirationsNextMonthGenerationMode NextMonthGenerationMode { get; set; }
    public List<ExpirationsAssistant> Assistants { get; set; } = [];
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ExpirationsBrokerCatalogItem
{
    public Guid BrokerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> PrimaryEmailAddresses { get; set; } = [];
    public bool IsActive { get; set; }
    public ExpirationsNextMonthGenerationMode NextMonthGenerationMode { get; set; }
    public List<ExpirationsAssistant> Assistants { get; set; } = [];
}

public enum ExpirationsNextMonthGenerationMode
{
    Standard,
    SpecialDualSorted
}

public sealed record ExpirationsBrokerCatalog(
    IReadOnlyList<ExpirationsBrokerCatalogItem> Items,
    IReadOnlyList<string> Warnings);

public enum ExpirationsAssociationKind
{
    Name,
    Alias,
    Code
}

public sealed class ExpirationsBrokerAssociation
{
    public Guid Id { get; set; }
    public Guid BrokerId { get; set; }
    public ExpirationsAssociationKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ExpirationsExclusion
{
    public Guid Id { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public enum ExpirationsProcess
{
    PreviousMonth,
    NextMonth
}

public sealed class ExpirationsProcessSettings
{
    public string DefaultSubject { get; set; } = string.Empty;
    public string DefaultMessage { get; set; } = string.Empty;
    public List<string> CommonCcAddresses { get; set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
