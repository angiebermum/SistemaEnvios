using System.Text.Json.Serialization;

namespace ECS.CommissionsMailer.Models;

public sealed class SentEmailRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BrokerId { get; set; }
    public string BrokerName { get; set; } = string.Empty;
    public List<string> BrokerPrimaryRecipients { get; set; } = [];
    public List<string> AssistantRecipients { get; set; } = [];
    public List<string> ToRecipients { get; set; } = [];
    public List<string> CcRecipients { get; set; } = [];
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTimeOffset SentAt { get; set; } = DateTimeOffset.Now;
    public List<string> ArchivedAttachmentPaths { get; set; } = [];
    public bool WasSuccessful { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public Guid? ResendOfRecordId { get; set; }
    public Guid? PaymentGenerationId { get; set; }

    [JsonIgnore]
    public string BrokerIdentityText
    {
        get
        {
            var primary = BrokerPrimaryRecipients.FirstOrDefault() ?? ToRecipients.FirstOrDefault();
            return string.IsNullOrWhiteSpace(primary) ? BrokerName : $"{BrokerName} — {primary}";
        }
    }

    [JsonIgnore]
    public string DisplayText => $"{SentAt:dd/MM/yyyy HH:mm} — {BrokerIdentityText} — {(WasSuccessful ? "Enviado" : "Error")}";
}
