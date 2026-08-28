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
    public List<ExpirationsAssistant> CancellationAssistants { get; set; } = [];
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
    public List<ExpirationsAssistant> CancellationAssistants { get; set; } = [];
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

public enum ExpirationsAssociationOrigin
{
    Confirmed,
    Imported,
    ManuallyConfirmed,
    ManuallyAdded
}

public enum ExpirationsDestinationGroup
{
    Principal,
    Personales,
    Generales,
    Agencias,
    PcGuanacaste,
    ContadoCoriMotors,
    VariosCoriMotors,
    HernanVarela
}

public readonly record struct ExpirationsDestinationKey(
    Guid BrokerId,
    ExpirationsDestinationGroup DestinationGroup);

public static class ExpirationsDestinationGroups
{
    public static string DisplayName(ExpirationsDestinationGroup group) => group switch
    {
        ExpirationsDestinationGroup.Principal => "Principal",
        ExpirationsDestinationGroup.Personales => "Personales",
        ExpirationsDestinationGroup.Generales => "Generales",
        ExpirationsDestinationGroup.Agencias => "Agencias",
        ExpirationsDestinationGroup.PcGuanacaste => "PC Guanacaste",
        ExpirationsDestinationGroup.ContadoCoriMotors => "Contado Cori Motors",
        ExpirationsDestinationGroup.VariosCoriMotors => "Varios Cori Motors",
        ExpirationsDestinationGroup.HernanVarela => "Hernán Varela",
        _ => group.ToString()
    };
}

public sealed class ExpirationsBrokerAssociation
{
    public Guid Id { get; set; }
    public Guid BrokerId { get; set; }
    public ExpirationsAssociationKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public ExpirationsAssociationOrigin Origin { get; set; } = ExpirationsAssociationOrigin.Confirmed;
    public ExpirationsDestinationGroup? DestinationGroup { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ExpirationsObservedIdentifier
{
    public Guid Id { get; set; }
    public Guid BrokerId { get; set; }
    public ExpirationsAssociationKind Kind { get; set; }
    public string Value { get; set; } = string.Empty;
    public string NormalizedValue { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
    public bool IsIgnored { get; set; }
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
    NextMonth,
    Cancellations
}

public sealed class ExpirationsProcessSettings
{
    public string DefaultSubject { get; set; } = string.Empty;
    public string DefaultMessage { get; set; } = string.Empty;
    public List<string> CommonCcAddresses { get; set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
