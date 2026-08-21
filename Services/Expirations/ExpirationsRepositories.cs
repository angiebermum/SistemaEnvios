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
