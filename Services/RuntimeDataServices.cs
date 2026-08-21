using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Diagnostics;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public sealed record RuntimeApplicationSnapshot(
    AppConfiguration Configuration,
    CurrentSession? CurrentSession,
    List<SentEmailRecord> RecentSends,
    List<PaymentGenerationBatch> PaymentGenerations,
    IReadOnlyList<string> Warnings);

public interface IRuntimeDataService
{
    RuntimeDataMode Mode { get; }
    AppUser? CurrentUser { get; }
    Task<RuntimeApplicationSnapshot> LoadAsync(CancellationToken cancellationToken = default);
    Task<AppConfiguration> ReloadConfigurationAsync(CancellationToken cancellationToken = default);
    Task SaveConfigurationAsync(AppConfiguration configuration, CancellationToken cancellationToken = default);
    Task SaveCurrentSessionAsync(CurrentSession session, CancellationToken cancellationToken = default);
    Task SaveRecentSendsAsync(IEnumerable<SentEmailRecord> records, CancellationToken cancellationToken = default);
    Task SavePaymentGenerationsAsync(IEnumerable<PaymentGenerationBatch> batches, CancellationToken cancellationToken = default);
}

public sealed class JsonOnlyRuntimeDataService : IRuntimeDataService
{
    private readonly ConfigurationService _configuration;
    private readonly SessionService _session;
    private readonly GenerationHistoryService _history;

    public JsonOnlyRuntimeDataService(AppDataPaths paths, FileLogger logger)
    {
        _configuration = new ConfigurationService(paths, logger);
        _session = new SessionService(paths, logger);
        _history = new GenerationHistoryService(paths, logger);
    }

    public RuntimeDataMode Mode => RuntimeDataMode.JsonOnly;
    public AppUser? CurrentUser => null;

    public Task<RuntimeApplicationSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var configuration = _configuration.Load();
        var session = _session.LoadCurrent();
        var recentSends = _session.LoadRecentSends();
        var generations = _history.Load();
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(_configuration.LastWarning)) warnings.Add(_configuration.LastWarning);
        warnings.AddRange(_session.Warnings);
        warnings.AddRange(_history.Warnings);
        return Task.FromResult(new RuntimeApplicationSnapshot(
            configuration,
            session,
            recentSends,
            generations,
            warnings));
    }

    public Task<AppConfiguration> ReloadConfigurationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_configuration.Load());
    }

    public Task SaveConfigurationAsync(AppConfiguration configuration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _configuration.Save(configuration);
        return Task.CompletedTask;
    }

    public Task SaveCurrentSessionAsync(CurrentSession session, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session.SaveCurrent(session);
        return Task.CompletedTask;
    }

    public Task SaveRecentSendsAsync(IEnumerable<SentEmailRecord> records, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _session.SaveRecentSends(records);
        return Task.CompletedTask;
    }

    public Task SavePaymentGenerationsAsync(IEnumerable<PaymentGenerationBatch> batches, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _history.Save(batches);
        return Task.CompletedTask;
    }
}

internal sealed class FirebaseClientLogAdapter(FileLogger logger) : IFirebaseClientLog
{
    public void Info(string message) => logger.Info(message);
    public void Warning(string message) => logger.Info($"ADVERTENCIA: {message}");
    public void Error(string message, Exception? exception = null)
    {
        if (exception is null) logger.Error(message);
        else logger.Error(message, exception);
    }
}

internal sealed class FirestoreRuntimeState
{
    public bool HasCloudWrites { get; set; }
    public DateTimeOffset? FirstCloudWriteAtUtc { get; set; }
    public DateTimeOffset? LastCloudWriteAtUtc { get; set; }
}

internal sealed class FirestoreRuntimeStateStore
{
    private readonly AppDataPaths _paths;
    private readonly AtomicJsonFile _json;

    public FirestoreRuntimeStateStore(AppDataPaths paths, FileLogger logger)
    {
        _paths = paths;
        _json = new AtomicJsonFile(logger);
    }

    public FirestoreRuntimeState Load() =>
        _json.Load<FirestoreRuntimeState>(_paths.FirestoreRuntimeStateFile, out _) ?? new FirestoreRuntimeState();

    public void RecordCloudWrite(FirestoreRuntimeState state)
    {
        var now = DateTimeOffset.UtcNow;
        state.HasCloudWrites = true;
        state.FirstCloudWriteAtUtc ??= now;
        state.LastCloudWriteAtUtc = now;
        _json.Save(_paths.FirestoreRuntimeStateFile, state);
    }
}
