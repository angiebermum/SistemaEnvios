namespace ECS.CommissionsMailer.Models;

public sealed class BrokerAssistant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

    public BrokerAssistant Clone() => new()
    {
        Id = Id,
        Name = Name,
        Email = Email,
        IsActive = IsActive
    };
}
