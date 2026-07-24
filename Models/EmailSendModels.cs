namespace ECS.CommissionsMailer.Models;

public sealed class EmailSendRequest
{
    public Guid RequestId { get; init; } = Guid.NewGuid();
    public Guid BrokerId { get; init; }
    public string BrokerName { get; init; } = string.Empty;
    public IReadOnlyList<string> BrokerPrimaryRecipients { get; init; } = [];
    public IReadOnlyList<string> AssistantRecipients { get; init; } = [];
    public IReadOnlyList<string> ToRecipients { get; init; } = [];
    public IReadOnlyList<string> CcRecipients { get; init; } = [];
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    public IReadOnlyList<string> AttachmentPaths { get; init; } = [];
    public string? SignatureImagePath { get; init; }
    public bool RequiresReview { get; init; }
    public string ReviewNote { get; init; } = string.Empty;
    public bool ReviewConfirmed { get; init; }
    public Guid? ResendOfRecordId { get; init; }
}

public sealed class RecipientResolutionResult
{
    public List<string> BrokerPrimaryRecipients { get; init; } = [];
    public List<string> AssistantRecipients { get; init; } = [];
    public List<string> ToRecipients { get; init; } = [];
    public List<string> CcRecipients { get; init; } = [];
    public List<string> Errors { get; init; } = [];
}

public sealed class EmailSendResult
{
    public Guid RequestId { get; init; }
    public bool WasSuccessful { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;
}

public enum OutlookProgressStage
{
    Starting,
    Completed
}

public sealed class OutlookSendProgress
{
    public required EmailSendRequest Request { get; init; }
    public required int Current { get; init; }
    public required int Total { get; init; }
    public required OutlookProgressStage Stage { get; init; }
    public EmailSendResult? Result { get; init; }
}
