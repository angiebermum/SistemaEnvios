using System.Text.Json;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

internal sealed class FirestorePrimaryRuntimeDataService : IRuntimeDataService
{
    private readonly FirestoreSettingsRepository<AppConfiguration> _settings;
    private readonly FirestoreBrokerRepository<Broker> _brokers;
    private readonly FirestoreSessionRepository<CurrentSession, BrokerSendItem> _sessions;
    private readonly FirestoreRecentSendRepository<SentEmailRecord> _recentSends;
    private readonly FirestorePaymentGenerationRepository<PaymentGenerationBatch, FirestoreGenerationFile> _generations;
    private readonly LocalWorkspaceStore _localStore;
    private readonly FirestoreRuntimeStateStore _runtimeStateStore;
    private readonly FirestoreRuntimeState _runtimeState;
    private readonly RuntimeApplicationSnapshot _legacySnapshot;
    private readonly AppDataPaths _paths;
    private readonly FileLogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _fingerprints = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _fullyLoadedFileGenerations = [];
    private LocalWorkspaceState? _local;

    public FirestorePrimaryRuntimeDataService(
        IFirestoreRestClient client,
        AppUser currentUser,
        RuntimeApplicationSnapshot legacySnapshot,
        AppDataPaths paths,
        FileLogger logger)
    {
        CurrentUser = currentUser ?? throw new ArgumentNullException(nameof(currentUser));
        AppUserAuthorization.DemandCommissionsAccess(currentUser);
        _legacySnapshot = legacySnapshot;
        _paths = paths;
        _logger = logger;
        _settings = new FirestoreSettingsRepository<AppConfiguration>(client, new FirestoreSettingsMapper());
        _brokers = new FirestoreBrokerRepository<Broker>(client, new FirestoreBrokerMapper());
        _sessions = new FirestoreSessionRepository<CurrentSession, BrokerSendItem>(
            client, new FirestoreCurrentSessionMapper(), new FirestoreBrokerSendItemMapper());
        _recentSends = new FirestoreRecentSendRepository<SentEmailRecord>(client, new FirestoreRecentSendMapper());
        _generations = new FirestorePaymentGenerationRepository<PaymentGenerationBatch, FirestoreGenerationFile>(
            client, new FirestorePaymentGenerationMapper(), new FirestorePaymentGenerationFileMapper());
        _localStore = new LocalWorkspaceStore(paths, logger);
        _runtimeStateStore = new FirestoreRuntimeStateStore(paths, logger);
        _runtimeState = _runtimeStateStore.Load();
    }

    public RuntimeDataMode Mode => RuntimeDataMode.FirestorePrimary;
    public AppUser? CurrentUser { get; }
    public bool HasCloudWrites => _runtimeState.HasCloudWrites;

    public async Task<RuntimeApplicationSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        var settingsTask = _settings.GetAsync(cancellationToken);
        var brokersTask = _brokers.ListAsync(cancellationToken: cancellationToken);
        var sessionTask = _sessions.GetCurrentAsync(cancellationToken);
        var itemsTask = _sessions.ListBrokerItemsAsync(cancellationToken);
        var recentTask = _recentSends.ListAsync(cancellationToken: cancellationToken);
        var generationsTask = _generations.ListAsync(cancellationToken);

        await Task.WhenAll(settingsTask, brokersTask, sessionTask, itemsTask, recentTask, generationsTask);
        var settings = await settingsTask ?? throw Missing("settings/commissions");
        var session = await sessionTask ?? throw Missing("sessions/current");
        var brokers = await brokersTask;
        var items = await itemsTask;
        var recent = await recentTask;
        var generationHeaders = await generationsTask;

        _local = File.Exists(_paths.LocalWorkspaceStateFile)
            ? _localStore.Load()
            : LocalWorkspaceStore.FromLegacy(
                _legacySnapshot.Configuration,
                _legacySnapshot.CurrentSession,
                _legacySnapshot.RecentSends,
                _legacySnapshot.PaymentGenerations,
                RuntimeDeterministicDocumentIds.ForGenerationFile);

        var configuration = settings.Value;
        configuration.Brokers = brokers.Select(value => value.Value).ToList();
        configuration.SignatureImagePath = _local.SignatureImagePath;

        var currentSession = session.Value;
        currentSession.BrokerItems = items.Select(value => value.Value).ToList();
        currentSession.GeneralWorkbookPath = _local.GeneralWorkbookPath;
        currentSession.GeneratedOutputDirectory = _local.GeneratedOutputDirectory;
        foreach (var item in currentSession.BrokerItems)
        {
            if (_local.BrokerItems.TryGetValue(item.BrokerId, out var localItem))
            {
                item.AttachmentPaths = new System.Collections.ObjectModel.ObservableCollection<string>(localItem.AttachmentPaths);
                item.GeneratedAttachmentPaths = new System.Collections.ObjectModel.ObservableCollection<string>(
                    localItem.GeneratedAttachmentPaths.Where(localItem.AttachmentPaths.Contains));
            }
            item.RefreshComputedProperties();
        }

        var recentSends = recent.Select(value => value.Value).OrderByDescending(value => value.SentAt).ToList();
        foreach (var send in recentSends)
        {
            if (_local.RecentSendArchivedAttachmentPaths.TryGetValue(send.Id, out var paths))
                send.ArchivedAttachmentPaths = [.. paths];
        }

        var paymentGenerations = generationHeaders.Select(value => value.Value)
            .OrderByDescending(value => value.CreatedAt)
            .ToList();
        var active = currentSession.ActivePaymentGenerationId.HasValue
            ? paymentGenerations.FirstOrDefault(value => value.Id == currentSession.ActivePaymentGenerationId.Value)
            : null;
        IReadOnlyList<FirestoreStoredDocument<FirestoreGenerationFile>> activeFiles = [];
        if (active is not null)
        {
            activeFiles = await _generations.ListFilesAsync(active.Id.ToString("D"), cancellationToken);
            active.Files = activeFiles.Select(value => value.Value.Value).ToList();
            if (_local.PaymentGenerations.TryGetValue(active.Id, out var localGeneration))
            {
                active.SourceWorkbookPath = localGeneration.SourceWorkbookPath;
                active.OutputDirectory = localGeneration.OutputDirectory;
                foreach (var stored in activeFiles)
                {
                    if (localGeneration.OutputPathsByFileId.TryGetValue(stored.Value.Id, out var outputPath))
                        stored.Value.Value.OutputPath = outputPath;
                }
            }
        }

        foreach (var generation in paymentGenerations)
        {
            if (_local.PaymentGenerations.TryGetValue(generation.Id, out var localGeneration))
            {
                generation.SourceWorkbookPath = localGeneration.SourceWorkbookPath;
                generation.OutputDirectory = localGeneration.OutputDirectory;
            }
        }

        _tokens.Clear();
        _fingerprints.Clear();
        _fullyLoadedFileGenerations.Clear();
        Track(settings);
        Track(session);
        Track(brokers);
        Track(items);
        Track(recent);
        Track(generationHeaders);
        Track(activeFiles);
        if (active is not null)
            _fullyLoadedFileGenerations.Add(active.Id);
        _logger.Info(
            $"FirestorePrimary cargado. brokers={brokers.Count}; brokerItems={items.Count}; " +
            $"recentSends={recent.Count}; generations={generationHeaders.Count}; activeFiles={active?.Files.Count ?? 0}.");
        return new RuntimeApplicationSnapshot(
            configuration,
            currentSession,
            recentSends,
            paymentGenerations,
            []);
    }

    public async Task<AppConfiguration> ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var settingsTask = _settings.GetAsync(cancellationToken);
        var brokersTask = _brokers.ListAsync(cancellationToken: cancellationToken);
        await Task.WhenAll(settingsTask, brokersTask);
        var settings = await settingsTask ?? throw Missing("settings/commissions");
        var brokers = await brokersTask;
        var result = settings.Value;
        result.Brokers = brokers.Select(value => value.Value).ToList();
        result.SignatureImagePath = EnsureLocal().SignatureImagePath;
        Track(settings);
        Track(brokers);
        return result;
    }

    public async Task SaveConfigurationAsync(AppConfiguration configuration, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var settingsPath = "settings/commissions";
            if (Changed(settingsPath, new FirestoreSettingsMapper().ToFields(configuration)))
            {
                var stored = await _settings.UpdateAsync(configuration, Token(settingsPath), cancellationToken);
                Track(stored);
                RecordCloudWrite();
            }

            var currentIds = configuration.Brokers.Select(value => value.Id.ToString("D")).ToHashSet(StringComparer.Ordinal);
            foreach (var broker in configuration.Brokers)
            {
                var id = broker.Id.ToString("D");
                var path = $"brokers/{id}";
                if (!_tokens.ContainsKey(path))
                {
                    Track(await _brokers.CreateAsync(id, broker, cancellationToken));
                    RecordCloudWrite();
                }
                else if (Changed(path, new FirestoreBrokerMapper().ToFields(broker)))
                {
                    Track(await _brokers.UpdateAsync(id, broker, Token(path), cancellationToken));
                    RecordCloudWrite();
                }
            }

            foreach (var path in _tokens.Keys.Where(value => value.StartsWith("brokers/", StringComparison.Ordinal)).ToList())
            {
                var id = path["brokers/".Length..];
                if (!currentIds.Contains(id))
                {
                    await _brokers.DeleteAsync(id, Token(path), cancellationToken);
                    _tokens.Remove(path);
                    _fingerprints.Remove(path);
                    RecordCloudWrite();
                }
            }

            EnsureLocal().SignatureImagePath = configuration.SignatureImagePath;
            _localStore.Save(EnsureLocal());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveCurrentSessionAsync(CurrentSession session, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            const string currentPath = "sessions/current";
            if (Changed(currentPath, new FirestoreCurrentSessionMapper().ToFields(session)))
            {
                session.SavedAt = DateTimeOffset.Now;
                Track(await _sessions.UpdateCurrentAsync(session, Token(currentPath), cancellationToken));
                RecordCloudWrite();
            }

            var currentItemIds = session.BrokerItems.Select(item => item.BrokerId.ToString("D"))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var item in session.BrokerItems)
            {
                var id = item.BrokerId.ToString("D");
                var path = $"sessions/current/brokerItems/{id}";
                if (!_tokens.ContainsKey(path))
                {
                    Track(await _sessions.CreateBrokerItemAsync(id, item, cancellationToken));
                    RecordCloudWrite();
                }
                else if (Changed(path, new FirestoreBrokerSendItemMapper().ToFields(item)))
                {
                    Track(await _sessions.UpdateBrokerItemAsync(id, item, Token(path), cancellationToken));
                    RecordCloudWrite();
                }

                EnsureLocal().BrokerItems[item.BrokerId] = new BrokerItemLocalWorkspace
                {
                    AttachmentPaths = [.. item.AttachmentPaths],
                    GeneratedAttachmentPaths = [.. item.GeneratedAttachmentPaths]
                };
            }

            const string itemPrefix = "sessions/current/brokerItems/";
            foreach (var path in _tokens.Keys.Where(value => value.StartsWith(itemPrefix, StringComparison.Ordinal)).ToList())
            {
                var id = path[itemPrefix.Length..];
                if (!currentItemIds.Contains(id))
                {
                    await _sessions.DeleteBrokerItemAsync(id, Token(path), cancellationToken);
                    _tokens.Remove(path);
                    _fingerprints.Remove(path);
                    if (Guid.TryParse(id, out var brokerId)) EnsureLocal().BrokerItems.Remove(brokerId);
                    RecordCloudWrite();
                }
            }

            EnsureLocal().GeneralWorkbookPath = session.GeneralWorkbookPath;
            EnsureLocal().GeneratedOutputDirectory = session.GeneratedOutputDirectory;
            _localStore.Save(EnsureLocal());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SaveRecentSendsAsync(IEnumerable<SentEmailRecord> records, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var materialized = records.OrderByDescending(value => value.SentAt).ToList();
            foreach (var send in materialized)
                if (send.Id == Guid.Empty) send.Id = RuntimeDeterministicDocumentIds.ForRecentSend(send);
            var currentIds = materialized.Select(value => value.Id.ToString("D")).ToHashSet(StringComparer.Ordinal);
            foreach (var send in materialized)
            {
                var id = send.Id.ToString("D");
                var path = $"recentSends/{id}";
                if (!_tokens.ContainsKey(path))
                {
                    Track(await _recentSends.CreateAsync(id, send, cancellationToken));
                    RecordCloudWrite();
                }
                else if (Changed(path, new FirestoreRecentSendMapper().ToFields(send)))
                {
                    Track(await _recentSends.UpdateAsync(id, send, Token(path), cancellationToken));
                    RecordCloudWrite();
                }

                EnsureLocal().RecentSendArchivedAttachmentPaths[send.Id] = [.. send.ArchivedAttachmentPaths];
            }

            foreach (var path in _tokens.Keys.Where(value => value.StartsWith("recentSends/", StringComparison.Ordinal)).ToList())
            {
                var id = path["recentSends/".Length..];
                if (!currentIds.Contains(id))
                {
                    await _recentSends.DeleteAsync(id, Token(path), cancellationToken);
                    _tokens.Remove(path);
                    _fingerprints.Remove(path);
                    if (Guid.TryParse(id, out var guid)) EnsureLocal().RecentSendArchivedAttachmentPaths.Remove(guid);
                    RecordCloudWrite();
                }
            }
            _localStore.Save(EnsureLocal());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SavePaymentGenerationsAsync(
        IEnumerable<PaymentGenerationBatch> batches,
        CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            foreach (var generation in batches)
            {
                var id = generation.Id.ToString("D");
                var path = $"paymentGenerations/{id}";
                if (!_tokens.ContainsKey(path))
                {
                    Track(await _generations.CreateAsync(id, generation, cancellationToken));
                    RecordCloudWrite();
                }
                else if (Changed(path, new FirestorePaymentGenerationMapper().ToFields(generation)))
                {
                    Track(await _generations.UpdateAsync(id, generation, Token(path), cancellationToken));
                    RecordCloudWrite();
                }

                var local = EnsureLocal().PaymentGenerations.TryGetValue(generation.Id, out var existingLocal)
                    ? existingLocal
                    : new GenerationLocalWorkspace();
                local.SourceWorkbookPath = generation.SourceWorkbookPath;
                local.OutputDirectory = generation.OutputDirectory;
                var currentFileIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in generation.Files)
                {
                    var fileId = RuntimeDeterministicDocumentIds.ForGenerationFile(generation.Id, file);
                    currentFileIds.Add(fileId.ToString("D"));
                    var filePath = $"paymentGenerations/{id}/files/{fileId:D}";
                    var wrapped = new FirestoreGenerationFile(fileId, file);
                    if (!_tokens.ContainsKey(filePath))
                    {
                        Track(await _generations.CreateFileAsync(id, fileId.ToString("D"), wrapped, cancellationToken));
                        RecordCloudWrite();
                    }
                    else if (Changed(filePath, new FirestorePaymentGenerationFileMapper().ToFields(wrapped)))
                    {
                        Track(await _generations.UpdateFileAsync(
                            id, fileId.ToString("D"), wrapped, Token(filePath), cancellationToken));
                        RecordCloudWrite();
                    }
                    local.OutputPathsByFileId[fileId] = file.OutputPath;
                }
                if (generation.Files.Count > 0)
                    _fullyLoadedFileGenerations.Add(generation.Id);

                if (_fullyLoadedFileGenerations.Contains(generation.Id))
                {
                    var prefix = $"paymentGenerations/{id}/files/";
                    foreach (var stalePath in _tokens.Keys.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    {
                        var staleId = stalePath[prefix.Length..];
                        if (!currentFileIds.Contains(staleId))
                        {
                            await _generations.DeleteFileAsync(id, staleId, Token(stalePath), cancellationToken);
                            _tokens.Remove(stalePath);
                            _fingerprints.Remove(stalePath);
                            if (Guid.TryParse(staleId, out var staleGuid)) local.OutputPathsByFileId.Remove(staleGuid);
                            RecordCloudWrite();
                        }
                    }
                }
                EnsureLocal().PaymentGenerations[generation.Id] = local;
            }
            _localStore.Save(EnsureLocal());
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private LocalWorkspaceState EnsureLocal() => _local ?? throw new InvalidOperationException(
        "Debe cargar el estado runtime antes de guardar.");

    private string Token(string path) => _tokens.TryGetValue(path, out var token)
        ? token
        : throw new InvalidOperationException($"No existe concurrency token para '{path}'. Recargue la información.");

    private bool Changed(string path, IReadOnlyDictionary<string, FirestoreRestValue> fields) =>
        !_fingerprints.TryGetValue(path, out var original) ||
        !string.Equals(original, Fingerprint(path, fields), StringComparison.Ordinal);

    private static string Fingerprint(string path, IReadOnlyDictionary<string, FirestoreRestValue> fields) =>
        JsonSerializer.Serialize(
            fields
                .Where(value => path != "sessions/current" || value.Key != "savedAtUtc")
                .OrderBy(value => value.Key, StringComparer.Ordinal)
                .ToDictionary(value => value.Key, value => value.Value),
            FirestoreRestJson.Options);

    private void Track<T>(FirestoreStoredDocument<T> document)
    {
        _tokens[document.DocumentPath] = document.UpdateTime;
        if (TryMapFields(document.Value, out var fields))
            _fingerprints[document.DocumentPath] = Fingerprint(document.DocumentPath, fields);
    }

    private void Track<T>(IEnumerable<FirestoreStoredDocument<T>> documents)
    {
        foreach (var document in documents) Track(document);
    }

    private static bool TryMapFields<T>(T value, out IReadOnlyDictionary<string, FirestoreRestValue> fields)
    {
        fields = value switch
        {
            AppConfiguration settings => new FirestoreSettingsMapper().ToFields(settings),
            Broker broker => new FirestoreBrokerMapper().ToFields(broker),
            CurrentSession session => new FirestoreCurrentSessionMapper().ToFields(session),
            BrokerSendItem item => new FirestoreBrokerSendItemMapper().ToFields(item),
            SentEmailRecord send => new FirestoreRecentSendMapper().ToFields(send),
            PaymentGenerationBatch generation => new FirestorePaymentGenerationMapper().ToFields(generation),
            FirestoreGenerationFile file => new FirestorePaymentGenerationFileMapper().ToFields(file),
            _ => new Dictionary<string, FirestoreRestValue>()
        };
        return fields.Count > 0;
    }

    private void RecordCloudWrite() => _runtimeStateStore.RecordCloudWrite(_runtimeState);

    private static InvalidDataException Missing(string path) =>
        new($"Falta el documento operativo obligatorio '{path}' en Firestore.");
}
