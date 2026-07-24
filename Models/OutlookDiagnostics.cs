namespace ECS.CommissionsMailer.Models;

public enum OutlookFailureReason
{
    None,
    ProgIdNotRegistered,
    ComClassNotRegistered,
    IntegrationAssemblyMissing,
    ArchitectureMismatch,
    ProfileUnavailable,
    SendingAccountUnavailable,
    ElevationMismatch,
    StaViolation,
    RecipientResolution,
    AttachmentFailure,
    SignatureFailure,
    SecurityOrPolicyRestriction,
    ModalDialog,
    ComFailure,
    UnexpectedError
}

public sealed record OutlookDiagnosticResult(
    bool WasSuccessful,
    string Message,
    OutlookFailureReason FailureReason = OutlookFailureReason.None,
    int? HResult = null);

public sealed record OutlookConnectionInfo(
    bool Available,
    string Message,
    string? EmailAddress = null);
