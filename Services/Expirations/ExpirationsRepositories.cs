using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsBrokerDirectoryRepository
{
    Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsBrokerDirectoryRepository(IFirestoreRestClient client)
    : IExpirationsBrokerDirectoryRepository
{
    private readonly FirestoreCollectionRepository<ExpirationsBrokerDirectoryEntry> _repository =
        new(client, new ExpirationsBrokerDirectoryMapper(), string.Empty, "brokers");

    public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(brokerId.ToString("D"), cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken: cancellationToken);
}

public interface IExpirationsBrokerProfileRepository
{
    Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
        ExpirationsBrokerProfile value,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
        ExpirationsBrokerProfile value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsBrokerProfileRepository(IFirestoreRestClient client)
    : IExpirationsBrokerProfileRepository
{
    private readonly FirestoreCollectionRepository<ExpirationsBrokerProfile> _repository =
        new(client, new ExpirationsBrokerProfileMapper(), "modules/vencimientos", "brokerProfiles");

    public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(brokerId.ToString("D"), cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken: cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
        ExpirationsBrokerProfile value,
        CancellationToken cancellationToken = default) =>
        _repository.CreateAsync(value.BrokerId.ToString("D"), value, cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
        ExpirationsBrokerProfile value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        _repository.UpdateAsync(value.BrokerId.ToString("D"), value, expectedUpdateTime, cancellationToken);
}

public interface IExpirationsBrokerAssociationRepository
{
    Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>?> GetAsync(
        Guid associationId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>> ListAsync(
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> CreateAsync(
        ExpirationsBrokerAssociation value,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> UpdateAsync(
        ExpirationsBrokerAssociation value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task DeleteAsync(
        Guid associationId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsBrokerAssociationRepository(IFirestoreRestClient client)
    : IExpirationsBrokerAssociationRepository
{
    private readonly FirestoreCollectionRepository<ExpirationsBrokerAssociation> _repository =
        new(client, new ExpirationsBrokerAssociationMapper(), "modules/vencimientos", "associations");

    public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>?> GetAsync(
        Guid associationId,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(associationId.ToString("D"), cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken: cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> CreateAsync(
        ExpirationsBrokerAssociation value,
        CancellationToken cancellationToken = default) =>
        _repository.CreateAsync(value.Id.ToString("D"), value, cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> UpdateAsync(
        ExpirationsBrokerAssociation value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        _repository.UpdateAsync(value.Id.ToString("D"), value, expectedUpdateTime, cancellationToken);

    public Task DeleteAsync(
        Guid associationId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        _repository.DeleteAsync(associationId.ToString("D"), expectedUpdateTime, cancellationToken);
}

public interface IExpirationsExclusionRepository
{
    Task<FirestoreStoredDocument<ExpirationsExclusion>?> GetAsync(
        Guid exclusionId,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>> ListAsync(
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsExclusion>> CreateAsync(
        ExpirationsExclusion value,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsExclusion>> UpdateAsync(
        ExpirationsExclusion value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsExclusionRepository(IFirestoreRestClient client)
    : IExpirationsExclusionRepository
{
    private readonly FirestoreCollectionRepository<ExpirationsExclusion> _repository =
        new(client, new ExpirationsExclusionMapper(), "modules/vencimientos", "exclusions");

    public Task<FirestoreStoredDocument<ExpirationsExclusion>?> GetAsync(
        Guid exclusionId,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(exclusionId.ToString("D"), cancellationToken);

    public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>> ListAsync(
        CancellationToken cancellationToken = default) =>
        _repository.ListAsync(cancellationToken: cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsExclusion>> CreateAsync(
        ExpirationsExclusion value,
        CancellationToken cancellationToken = default) =>
        _repository.CreateAsync(value.Id.ToString("D"), value, cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsExclusion>> UpdateAsync(
        ExpirationsExclusion value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        _repository.UpdateAsync(value.Id.ToString("D"), value, expectedUpdateTime, cancellationToken);
}

public interface IExpirationsProcessSettingsRepository
{
    Task<FirestoreStoredDocument<ExpirationsProcessSettings>?> GetAsync(
        ExpirationsProcess process,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsProcessSettings>> CreateAsync(
        ExpirationsProcess process,
        ExpirationsProcessSettings value,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsProcessSettings>> UpdateAsync(
        ExpirationsProcess process,
        ExpirationsProcessSettings value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsProcessSettingsRepository(IFirestoreRestClient client)
    : IExpirationsProcessSettingsRepository
{
    private readonly FirestoreCollectionRepository<ExpirationsProcessSettings> _repository =
        new(client, new ExpirationsProcessSettingsMapper(), "modules/vencimientos", "settings");

    public Task<FirestoreStoredDocument<ExpirationsProcessSettings>?> GetAsync(
        ExpirationsProcess process,
        CancellationToken cancellationToken = default) =>
        _repository.GetAsync(DocumentId(process), cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> CreateAsync(
        ExpirationsProcess process,
        ExpirationsProcessSettings value,
        CancellationToken cancellationToken = default) =>
        _repository.CreateAsync(DocumentId(process), value, cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsProcessSettings>> UpdateAsync(
        ExpirationsProcess process,
        ExpirationsProcessSettings value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        _repository.UpdateAsync(DocumentId(process), value, expectedUpdateTime, cancellationToken);

    internal static string DocumentId(ExpirationsProcess process) => process switch
    {
        ExpirationsProcess.PreviousMonth => "previousMonth",
        ExpirationsProcess.NextMonth => "nextMonth",
        _ => throw new ArgumentOutOfRangeException(nameof(process), process, "Proceso de Vencimientos no permitido.")
    };
}

public interface IExpirationsSendHistoryRepository
{
    Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>> ListOperationsAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>> ListItemsAsync(
        Guid operationId,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsSendOperation>> CreateOperationAsync(
        ExpirationsSendOperation value,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> CreateItemAsync(
        Guid operationId,
        ExpirationsSendHistoryItem value,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsSendOperation>> UpdateOperationAsync(
        ExpirationsSendOperation value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> UpdateItemAsync(
        Guid operationId,
        ExpirationsSendHistoryItem value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsSendHistoryRepository : IExpirationsSendHistoryRepository
{
    private readonly IFirestoreRestClient _client;
    private readonly FirestoreCollectionRepository<ExpirationsSendOperation> _operations;
    private readonly ExpirationsSendHistoryItemMapper _itemMapper = new();

    public ExpirationsSendHistoryRepository(IFirestoreRestClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _operations = new FirestoreCollectionRepository<ExpirationsSendOperation>(
            client,
            new ExpirationsSendOperationMapper(),
            "modules/vencimientos",
            "sendOperations");
    }

    public async Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendOperation>>> ListOperationsAsync(
        CancellationToken cancellationToken = default) =>
        (await _operations.ListAsync(cancellationToken: cancellationToken))
            .OrderByDescending(document => document.Value.StartedAtUtc)
            .ThenByDescending(document => document.Value.OperationId)
            .ToList();

    public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsSendHistoryItem>>> ListItemsAsync(
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        Items(operationId).ListAsync(cancellationToken: cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsSendOperation>> CreateOperationAsync(
        ExpirationsSendOperation value,
        CancellationToken cancellationToken = default) =>
        _operations.CreateAsync(value.OperationId.ToString("D"), value, cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> CreateItemAsync(
        Guid operationId,
        ExpirationsSendHistoryItem value,
        CancellationToken cancellationToken = default) =>
        Items(operationId).CreateAsync(value.ItemId.ToString("D"), value, cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsSendOperation>> UpdateOperationAsync(
        ExpirationsSendOperation value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        _operations.UpdateAsync(value.OperationId.ToString("D"), value, expectedUpdateTime, cancellationToken);

    public Task<FirestoreStoredDocument<ExpirationsSendHistoryItem>> UpdateItemAsync(
        Guid operationId,
        ExpirationsSendHistoryItem value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        Items(operationId).UpdateAsync(value.ItemId.ToString("D"), value, expectedUpdateTime, cancellationToken);

    private FirestoreCollectionRepository<ExpirationsSendHistoryItem> Items(Guid operationId) =>
        new(
            _client,
            _itemMapper,
            $"modules/vencimientos/sendOperations/{operationId:D}",
            "items");
}
