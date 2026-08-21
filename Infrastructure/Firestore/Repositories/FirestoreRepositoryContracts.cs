using ECS.CommissionsMailer.Infrastructure.Firestore.Models;
using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.Firestore.Repositories;

public sealed record FirestoreStoredDocument<T>(T Value, Timestamp UpdateTime);

public sealed class FirestoreConcurrencyException : InvalidOperationException
{
    public FirestoreConcurrencyException(string documentPath, Timestamp expected, Timestamp actual)
        : base($"El documento '{documentPath}' cambió desde la lectura anterior.")
    {
        DocumentPath = documentPath;
        Expected = expected;
        Actual = actual;
    }

    public string DocumentPath { get; }
    public Timestamp Expected { get; }
    public Timestamp Actual { get; }
}

public interface IFirestoreBrokerRepository
{
    Task<FirestoreStoredDocument<FirestoreBrokerDocument>?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FirestoreStoredDocument<FirestoreBrokerDocument>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Timestamp> CreateAsync(
        FirestoreBrokerDocument broker,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        FirestoreBrokerDocument broker,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public interface IFirestoreSettingsRepository
{
    Task<FirestoreStoredDocument<FirestoreCommissionsSettingsDocument>?> GetAsync(
        CancellationToken cancellationToken = default);

    Task<Timestamp> CreateAsync(
        FirestoreCommissionsSettingsDocument settings,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        FirestoreCommissionsSettingsDocument settings,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public interface IFirestoreSessionRepository
{
    Task<FirestoreStoredDocument<FirestoreCurrentSessionDocument>?> GetCurrentAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FirestoreStoredDocument<FirestoreBrokerSendItemDocument>>> ListBrokerItemsAsync(
        CancellationToken cancellationToken = default);

    Task<Timestamp> CreateCurrentAsync(
        FirestoreCurrentSessionDocument session,
        CancellationToken cancellationToken = default);

    Task UpdateCurrentAsync(
        FirestoreCurrentSessionDocument session,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default);

    Task<Timestamp> CreateBrokerItemAsync(
        FirestoreBrokerSendItemDocument brokerItem,
        CancellationToken cancellationToken = default);

    Task UpdateBrokerItemAsync(
        FirestoreBrokerSendItemDocument brokerItem,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public interface IFirestoreRecentSendRepository
{
    Task<FirestoreStoredDocument<FirestoreRecentSendDocument>?> GetAsync(
        Guid sendId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FirestoreStoredDocument<FirestoreRecentSendDocument>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<Timestamp> CreateAsync(
        FirestoreRecentSendDocument send,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        FirestoreRecentSendDocument send,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public interface IFirestorePaymentGenerationRepository
{
    Task<FirestoreStoredDocument<FirestorePaymentGenerationDocument>?> GetAsync(
        Guid generationId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FirestoreStoredDocument<FirestorePaymentGenerationDocument>>> ListAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FirestoreStoredDocument<FirestorePaymentGenerationFileDocument>>> ListFilesAsync(
        Guid generationId,
        CancellationToken cancellationToken = default);

    Task<Timestamp> CreateAsync(
        FirestorePaymentGenerationDocument generation,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        FirestorePaymentGenerationDocument generation,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default);

    Task<Timestamp> CreateFileAsync(
        Guid generationId,
        FirestorePaymentGenerationFileDocument file,
        CancellationToken cancellationToken = default);

    Task UpdateFileAsync(
        Guid generationId,
        FirestorePaymentGenerationFileDocument file,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default);
}
