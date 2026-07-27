namespace ECS.CommissionsMailer.Models;

public sealed class Broker
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? SeedKey { get; set; }
    public string Name { get; set; } = string.Empty;
    public List<string> PrimaryEmailAddresses { get; set; } = [];
    public List<BrokerAssistant> Assistants { get; set; } = [];
    public List<string> AssociatedWorksheetNames { get; set; } = [];
    public List<BrokerDeduction> Deductions { get; set; } = [];
    public bool IsActive { get; set; } = true;
    public bool RequiresReview { get; set; }
    public string? ReviewNote { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string IdentityText => PrimaryEmailAddresses.Count == 0
        ? Name
        : $"{Name} — {string.Join("; ", PrimaryEmailAddresses)}";

    public Broker Clone() => new()
    {
        Id = Id,
        SeedKey = SeedKey,
        Name = Name,
        PrimaryEmailAddresses = [.. PrimaryEmailAddresses],
        Assistants = Assistants.Select(assistant => assistant.Clone()).ToList(),
        AssociatedWorksheetNames = [.. AssociatedWorksheetNames],
        Deductions = Deductions.Select(deduction => deduction.Clone()).ToList(),
        IsActive = IsActive,
        RequiresReview = RequiresReview,
        ReviewNote = ReviewNote
    };
}
