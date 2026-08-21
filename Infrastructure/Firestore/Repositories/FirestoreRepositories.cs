using ECS.CommissionsMailer.Infrastructure.Firestore.Models;
using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Infrastructure.Firestore.Repositories;

public abstract class FirestoreRepositoryBase(FirestoreDb database)
{
    protected FirestoreDb Database { get; } = database ?? throw new ArgumentNullException(nameof(database));

    protected static async Task<FirestoreStoredDocument<T>?> GetAsync<T>(
        DocumentReference reference,
        CancellationToken cancellationToken)
    {
        var snapshot = await reference.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Exists
            ? new FirestoreStoredDocument<T>(snapshot.ConvertTo<T>(), RequiredUpdateTime(snapshot))
            : null;
    }

    protected static async Task<IReadOnlyList<FirestoreStoredDocument<T>>> ListAsync<T>(
        Query query,
        CancellationToken cancellationToken)
    {
        var snapshot = await query.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Documents
            .Select(document => new FirestoreStoredDocument<T>(document.ConvertTo<T>(), RequiredUpdateTime(document)))
            .ToList();
    }

    protected static async Task<Timestamp> CreateAsync<T>(
        DocumentReference reference,
        T value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = await reference.CreateAsync(value, cancellationToken).ConfigureAwait(false);
        return result.UpdateTime;
    }

    protected async Task UpdateAsync<T>(
        DocumentReference reference,
        T value,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        await Database.RunTransactionAsync(async transaction =>
        {
            var snapshot = await transaction.GetSnapshotAsync(reference, cancellationToken).ConfigureAwait(false);
            if (!snapshot.Exists)
            {
                throw new InvalidOperationException($"El documento '{reference.Path}' no existe.");
            }

            var actualUpdateTime = RequiredUpdateTime(snapshot);
            if (actualUpdateTime != expectedUpdateTime)
            {
                throw new FirestoreConcurrencyException(
                    reference.Path,
                    expectedUpdateTime,
                    actualUpdateTime);
            }

            transaction.Set(reference, value, SetOptions.Overwrite);
            return true;
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static Timestamp RequiredUpdateTime(DocumentSnapshot snapshot) =>
        snapshot.UpdateTime ?? throw new InvalidOperationException(
            $"Firestore no devolvió updateTime para '{snapshot.Reference.Path}'.");
}

public sealed class FirestoreBrokerRepository(FirestoreDb database)
    : FirestoreRepositoryBase(database), IFirestoreBrokerRepository
{
    private CollectionReference Collection => Database.Collection(FirestorePaths.BrokersCollection);

    public Task<FirestoreStoredDocument<FirestoreBrokerDocument>?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default) =>
        GetAsync<FirestoreBrokerDocument>(Document(brokerId), cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<FirestoreBrokerDocument>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync<FirestoreBrokerDocument>(Collection, cancellationToken);

    public Task<Timestamp> CreateAsync(
        FirestoreBrokerDocument broker,
        CancellationToken cancellationToken = default) =>
        CreateAsync(Document(broker.Id), broker, cancellationToken);

    public Task UpdateAsync(
        FirestoreBrokerDocument broker,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(Document(broker.Id), broker, expectedUpdateTime, cancellationToken);

    private DocumentReference Document(Guid id) => Collection.Document(FirestorePaths.GuidDocumentId(id));
}

public sealed class FirestoreSettingsRepository(FirestoreDb database)
    : FirestoreRepositoryBase(database), IFirestoreSettingsRepository
{
    private DocumentReference Document => Database
        .Collection(FirestorePaths.SettingsCollection)
        .Document(FirestorePaths.CommissionsSettingsDocument);

    public Task<FirestoreStoredDocument<FirestoreCommissionsSettingsDocument>?> GetAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<FirestoreCommissionsSettingsDocument>(Document, cancellationToken);

    public Task<Timestamp> CreateAsync(
        FirestoreCommissionsSettingsDocument settings,
        CancellationToken cancellationToken = default) =>
        CreateAsync(Document, settings, cancellationToken);

    public Task UpdateAsync(
        FirestoreCommissionsSettingsDocument settings,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(Document, settings, expectedUpdateTime, cancellationToken);
}

public sealed class FirestoreSessionRepository(FirestoreDb database)
    : FirestoreRepositoryBase(database), IFirestoreSessionRepository
{
    private DocumentReference Current => Database
        .Collection(FirestorePaths.SessionsCollection)
        .Document(FirestorePaths.CurrentSessionDocument);

    private CollectionReference BrokerItems => Current.Collection(FirestorePaths.BrokerItemsSubcollection);

    public Task<FirestoreStoredDocument<FirestoreCurrentSessionDocument>?> GetCurrentAsync(
        CancellationToken cancellationToken = default) =>
        GetAsync<FirestoreCurrentSessionDocument>(Current, cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<FirestoreBrokerSendItemDocument>>> ListBrokerItemsAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync<FirestoreBrokerSendItemDocument>(BrokerItems, cancellationToken);

    public Task<Timestamp> CreateCurrentAsync(
        FirestoreCurrentSessionDocument session,
        CancellationToken cancellationToken = default) =>
        CreateAsync(Current, session, cancellationToken);

    public Task UpdateCurrentAsync(
        FirestoreCurrentSessionDocument session,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(Current, session, expectedUpdateTime, cancellationToken);

    public Task<Timestamp> CreateBrokerItemAsync(
        FirestoreBrokerSendItemDocument brokerItem,
        CancellationToken cancellationToken = default) =>
        CreateAsync(BrokerItem(brokerItem.BrokerId), brokerItem, cancellationToken);

    public Task UpdateBrokerItemAsync(
        FirestoreBrokerSendItemDocument brokerItem,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(BrokerItem(brokerItem.BrokerId), brokerItem, expectedUpdateTime, cancellationToken);

    private DocumentReference BrokerItem(Guid id) => BrokerItems.Document(FirestorePaths.GuidDocumentId(id));
}

public sealed class FirestoreRecentSendRepository(FirestoreDb database)
    : FirestoreRepositoryBase(database), IFirestoreRecentSendRepository
{
    private CollectionReference Collection => Database.Collection(FirestorePaths.RecentSendsCollection);

    public Task<FirestoreStoredDocument<FirestoreRecentSendDocument>?> GetAsync(
        Guid sendId,
        CancellationToken cancellationToken = default) =>
        GetAsync<FirestoreRecentSendDocument>(Document(sendId), cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<FirestoreRecentSendDocument>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync<FirestoreRecentSendDocument>(Collection, cancellationToken);

    public Task<Timestamp> CreateAsync(
        FirestoreRecentSendDocument send,
        CancellationToken cancellationToken = default) =>
        CreateAsync(Document(send.Id), send, cancellationToken);

    public Task UpdateAsync(
        FirestoreRecentSendDocument send,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(Document(send.Id), send, expectedUpdateTime, cancellationToken);

    private DocumentReference Document(Guid id) => Collection.Document(FirestorePaths.GuidDocumentId(id));
}

public sealed class FirestorePaymentGenerationRepository(FirestoreDb database)
    : FirestoreRepositoryBase(database), IFirestorePaymentGenerationRepository
{
    private CollectionReference Collection => Database.Collection(FirestorePaths.PaymentGenerationsCollection);

    public Task<FirestoreStoredDocument<FirestorePaymentGenerationDocument>?> GetAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        GetAsync<FirestorePaymentGenerationDocument>(Document(generationId), cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<FirestorePaymentGenerationDocument>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        ListAsync<FirestorePaymentGenerationDocument>(Collection, cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<FirestorePaymentGenerationFileDocument>>> ListFilesAsync(
        Guid generationId,
        CancellationToken cancellationToken = default) =>
        ListAsync<FirestorePaymentGenerationFileDocument>(Files(generationId), cancellationToken);

    public Task<Timestamp> CreateAsync(
        FirestorePaymentGenerationDocument generation,
        CancellationToken cancellationToken = default) =>
        CreateAsync(Document(generation.Id), generation, cancellationToken);

    public Task UpdateAsync(
        FirestorePaymentGenerationDocument generation,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(Document(generation.Id), generation, expectedUpdateTime, cancellationToken);

    public Task<Timestamp> CreateFileAsync(
        Guid generationId,
        FirestorePaymentGenerationFileDocument file,
        CancellationToken cancellationToken = default) =>
        CreateAsync(File(generationId, file.Id), file, cancellationToken);

    public Task UpdateFileAsync(
        Guid generationId,
        FirestorePaymentGenerationFileDocument file,
        Timestamp expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(File(generationId, file.Id), file, expectedUpdateTime, cancellationToken);

    private DocumentReference Document(Guid id) => Collection.Document(FirestorePaths.GuidDocumentId(id));

    private CollectionReference Files(Guid generationId) =>
        Document(generationId).Collection(FirestorePaths.GenerationFilesSubcollection);

    private DocumentReference File(Guid generationId, Guid fileId) =>
        Files(generationId).Document(FirestorePaths.GuidDocumentId(fileId));
}
