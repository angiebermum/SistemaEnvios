namespace ECS.CommissionsMailer.Infrastructure.Firestore.Models;

/// <summary>
/// Tipos sin atributos Firestore que conservan exclusivamente referencias físicas
/// propias de una computadora. Nunca deben enviarse a la base compartida.
/// </summary>
public sealed record ConfigurationLocalState(string? SignatureImagePath);

public sealed record CurrentSessionLocalState(
    string GeneralWorkbookPath,
    string GeneratedOutputDirectory);

public sealed record BrokerSendItemLocalState(
    Guid BrokerId,
    IReadOnlyList<string> AttachmentPaths,
    IReadOnlyList<string> GeneratedAttachmentPaths);

public sealed record RecentSendLocalState(
    Guid Id,
    IReadOnlyList<string> ArchivedAttachmentPaths);

public sealed record PaymentGenerationLocalState(
    Guid Id,
    string SourceWorkbookPath,
    string OutputDirectory);

public sealed record PaymentGenerationFileLocalState(
    Guid FirestoreFileId,
    string OutputPath);

public sealed record ConfigurationPersistencePartition(
    FirestoreCommissionsSettingsDocument SharedSettings,
    IReadOnlyList<FirestoreBrokerDocument> SharedBrokers,
    ConfigurationLocalState Local);

public sealed record CurrentSessionPersistencePartition(
    FirestoreCurrentSessionDocument SharedSession,
    IReadOnlyList<FirestoreBrokerSendItemDocument> SharedBrokerItems,
    CurrentSessionLocalState Local,
    IReadOnlyList<BrokerSendItemLocalState> LocalBrokerItems);

public sealed record RecentSendPersistencePartition(
    FirestoreRecentSendDocument Shared,
    RecentSendLocalState Local);

public sealed record PaymentGenerationPersistencePartition(
    FirestorePaymentGenerationDocument SharedGeneration,
    IReadOnlyList<FirestorePaymentGenerationFileDocument> SharedFiles,
    PaymentGenerationLocalState Local,
    IReadOnlyList<PaymentGenerationFileLocalState> LocalFiles);
