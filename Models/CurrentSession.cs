namespace ECS.CommissionsMailer.Models;

public sealed class CurrentSession
{
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string CommonCcText { get; set; } = string.Empty;
    public string GeneralWorkbookPath { get; set; } = string.Empty;
    public Guid? ActivePaymentGenerationId { get; set; }
    public string GeneratedOutputDirectory { get; set; } = string.Empty;
    public string GeneratedPeriod { get; set; } = string.Empty;
    public List<BrokerSendItem> BrokerItems { get; set; } = [];
    public DateTimeOffset SavedAt { get; set; } = DateTimeOffset.Now;
}
