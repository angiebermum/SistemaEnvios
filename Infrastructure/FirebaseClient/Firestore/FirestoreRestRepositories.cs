namespace ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;

public sealed record FirestoreStoredDocument<T>(T Value, string DocumentPath, string UpdateTime);

public interface IFirestoreEntityMapper<T>
{
    IReadOnlyDictionary<string, FirestoreRestValue> ToFields(T value);
    T FromFields(IReadOnlyDictionary<string, FirestoreRestValue> fields);
}

public class FirestoreCollectionRepository<T>
{
    private readonly IFirestoreRestClient _client;
    private readonly IFirestoreEntityMapper<T> _mapper;
    private readonly string _parentPath;
    private readonly string _collectionId;

    public FirestoreCollectionRepository(
        IFirestoreRestClient client,
        IFirestoreEntityMapper<T> mapper,
        string parentPath,
        string collectionId)
    {
        _client = client;
        _mapper = mapper;
        _parentPath = parentPath;
        _collectionId = collectionId;
    }

    protected string Path(string id) => string.IsNullOrEmpty(_parentPath)
        ? $"{_collectionId}/{id}"
        : $"{_parentPath}/{_collectionId}/{id}";

    public async Task<FirestoreStoredDocument<T>?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        var document = await _client.GetDocumentAsync(Path(id), cancellationToken);
        return document is null ? null : Convert(document);
    }

    public async Task<IReadOnlyList<FirestoreStoredDocument<T>>> ListAsync(
        int pageSize = 200,
        CancellationToken cancellationToken = default) =>
        (await _client.ListDocumentsAsync(_parentPath, _collectionId, pageSize, cancellationToken))
        .Select(Convert)
        .ToList();

    public async Task<FirestoreStoredDocument<T>> CreateAsync(
        string id,
        T value,
        CancellationToken cancellationToken = default) =>
        Convert(await _client.CreateDocumentAsync(
            _parentPath, _collectionId, id, _mapper.ToFields(value), cancellationToken));

    public async Task<FirestoreStoredDocument<T>> UpdateAsync(
        string id,
        T value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        Convert(await _client.UpdateDocumentAsync(
            Path(id), _mapper.ToFields(value), expectedUpdateTime, cancellationToken));

    public Task DeleteAsync(
        string id,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default) =>
        _client.DeleteDocumentAsync(Path(id), expectedUpdateTime, cancellationToken);

    private FirestoreStoredDocument<T> Convert(FirestoreRestDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.UpdateTime))
        {
            throw new InvalidDataException($"Firestore no devolvió updateTime para '{document.Name}'.");
        }

        var marker = "/documents/";
        var index = document.Name.IndexOf(marker, StringComparison.Ordinal);
        var path = index < 0 ? document.Name : document.Name[(index + marker.Length)..];
        return new FirestoreStoredDocument<T>(
            _mapper.FromFields(document.Fields),
            path,
            document.UpdateTime);
    }
}

public sealed class FirestoreSettingsRepository<T>(IFirestoreRestClient client, IFirestoreEntityMapper<T> mapper)
    : FirestoreCollectionRepository<T>(client, mapper, string.Empty, "settings")
{
    public Task<FirestoreStoredDocument<T>?> GetAsync(CancellationToken cancellationToken = default) =>
        GetAsync("commissions", cancellationToken);
    public Task<FirestoreStoredDocument<T>> CreateAsync(T value, CancellationToken cancellationToken = default) =>
        CreateAsync("commissions", value, cancellationToken);
    public Task<FirestoreStoredDocument<T>> UpdateAsync(T value, string token, CancellationToken cancellationToken = default) =>
        UpdateAsync("commissions", value, token, cancellationToken);
}

public sealed class FirestoreBrokerRepository<T>(IFirestoreRestClient client, IFirestoreEntityMapper<T> mapper)
    : FirestoreCollectionRepository<T>(client, mapper, string.Empty, "brokers");

public sealed class FirestoreSessionRepository<TSession, TBrokerItem>
{
    private readonly FirestoreCollectionRepository<TSession> _sessions;
    private readonly FirestoreCollectionRepository<TBrokerItem> _items;

    public FirestoreSessionRepository(
        IFirestoreRestClient client,
        IFirestoreEntityMapper<TSession> sessionMapper,
        IFirestoreEntityMapper<TBrokerItem> itemMapper)
    {
        _sessions = new FirestoreCollectionRepository<TSession>(client, sessionMapper, string.Empty, "sessions");
        _items = new FirestoreCollectionRepository<TBrokerItem>(client, itemMapper, "sessions/current", "brokerItems");
    }

    public Task<FirestoreStoredDocument<TSession>?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        _sessions.GetAsync("current", cancellationToken);
    public Task<FirestoreStoredDocument<TSession>> CreateCurrentAsync(TSession value, CancellationToken cancellationToken = default) =>
        _sessions.CreateAsync("current", value, cancellationToken);
    public Task<FirestoreStoredDocument<TSession>> UpdateCurrentAsync(TSession value, string token, CancellationToken cancellationToken = default) =>
        _sessions.UpdateAsync("current", value, token, cancellationToken);
    public Task<IReadOnlyList<FirestoreStoredDocument<TBrokerItem>>> ListBrokerItemsAsync(CancellationToken cancellationToken = default) =>
        _items.ListAsync(cancellationToken: cancellationToken);
    public Task<FirestoreStoredDocument<TBrokerItem>> CreateBrokerItemAsync(string id, TBrokerItem value, CancellationToken cancellationToken = default) =>
        _items.CreateAsync(id, value, cancellationToken);
    public Task<FirestoreStoredDocument<TBrokerItem>> UpdateBrokerItemAsync(string id, TBrokerItem value, string token, CancellationToken cancellationToken = default) =>
        _items.UpdateAsync(id, value, token, cancellationToken);
    public Task DeleteBrokerItemAsync(string id, string token, CancellationToken cancellationToken = default) =>
        _items.DeleteAsync(id, token, cancellationToken);
}

public sealed class FirestoreRecentSendRepository<T>(IFirestoreRestClient client, IFirestoreEntityMapper<T> mapper)
    : FirestoreCollectionRepository<T>(client, mapper, string.Empty, "recentSends");

public sealed class FirestorePaymentGenerationRepository<TGeneration, TFile>
{
    private readonly IFirestoreRestClient _client;
    private readonly IFirestoreEntityMapper<TGeneration> _generationMapper;
    private readonly IFirestoreEntityMapper<TFile> _fileMapper;
    private readonly FirestoreCollectionRepository<TGeneration> _generations;

    public FirestorePaymentGenerationRepository(
        IFirestoreRestClient client,
        IFirestoreEntityMapper<TGeneration> generationMapper,
        IFirestoreEntityMapper<TFile> fileMapper)
    {
        _client = client;
        _generationMapper = generationMapper;
        _fileMapper = fileMapper;
        _generations = new FirestoreCollectionRepository<TGeneration>(client, generationMapper, string.Empty, "paymentGenerations");
    }

    public Task<IReadOnlyList<FirestoreStoredDocument<TGeneration>>> ListAsync(CancellationToken cancellationToken = default) =>
        _generations.ListAsync(cancellationToken: cancellationToken);
    public Task<FirestoreStoredDocument<TGeneration>?> GetAsync(string id, CancellationToken cancellationToken = default) =>
        _generations.GetAsync(id, cancellationToken);
    public Task<FirestoreStoredDocument<TGeneration>> CreateAsync(string id, TGeneration value, CancellationToken cancellationToken = default) =>
        _generations.CreateAsync(id, value, cancellationToken);
    public Task<FirestoreStoredDocument<TGeneration>> UpdateAsync(string id, TGeneration value, string token, CancellationToken cancellationToken = default) =>
        _generations.UpdateAsync(id, value, token, cancellationToken);
    public Task<IReadOnlyList<FirestoreStoredDocument<TFile>>> ListFilesAsync(string generationId, CancellationToken cancellationToken = default) =>
        new FirestoreCollectionRepository<TFile>(_client, _fileMapper, $"paymentGenerations/{generationId}", "files")
            .ListAsync(cancellationToken: cancellationToken);
    public Task<FirestoreStoredDocument<TFile>> CreateFileAsync(string generationId, string fileId, TFile value, CancellationToken cancellationToken = default) =>
        new FirestoreCollectionRepository<TFile>(_client, _fileMapper, $"paymentGenerations/{generationId}", "files")
            .CreateAsync(fileId, value, cancellationToken);
    public Task<FirestoreStoredDocument<TFile>> UpdateFileAsync(string generationId, string fileId, TFile value, string token, CancellationToken cancellationToken = default) =>
        new FirestoreCollectionRepository<TFile>(_client, _fileMapper, $"paymentGenerations/{generationId}", "files")
            .UpdateAsync(fileId, value, token, cancellationToken);
    public Task DeleteFileAsync(string generationId, string fileId, string token, CancellationToken cancellationToken = default) =>
        new FirestoreCollectionRepository<TFile>(_client, _fileMapper, $"paymentGenerations/{generationId}", "files")
            .DeleteAsync(fileId, token, cancellationToken);
}
