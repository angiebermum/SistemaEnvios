using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class FirestoreComparisonSemanticsTests
{
    private static readonly DateTimeOffset LocalSavedAt =
        new(2026, 8, 10, 18, 38, 0, 31, TimeSpan.Zero);
    private static readonly DateTimeOffset RemoteSavedAt =
        new(2026, 8, 8, 0, 51, 32, 881, TimeSpan.Zero);

    [Fact]
    public void SessionDifferingOnlyBySavedAtIsFunctionalMatch()
    {
        var comparison = CompareSessions(Session(LocalSavedAt), Session(RemoteSavedAt));

        Assert.Equal(FirestoreComparisonStatus.NON_BLOCKING_METADATA_DIFFERENCE, comparison.Status);
        Assert.Equal(["savedAtUtc"], comparison.NonBlockingMetadataFields);
    }

    [Fact]
    public void SessionRemainsFunctionalMatchAcrossRepeatedSavedAtChanges()
    {
        var baseline = Session(RemoteSavedAt);
        foreach (var changed in new[]
                 {
                     LocalSavedAt,
                     LocalSavedAt.AddSeconds(1),
                     LocalSavedAt.AddHours(6),
                     LocalSavedAt.AddDays(1)
                 })
        {
            Assert.Equal(
                FirestoreComparisonStatus.NON_BLOCKING_METADATA_DIFFERENCE,
                CompareSessions(Session(changed), baseline).Status);
        }
    }

    [Fact]
    public void SubjectDifferenceStillBlocksWhenSavedAtAlsoDiffers()
    {
        var local = Session(LocalSavedAt);
        local.Subject = "Asunto cambiado";

        Assert.Equal(FirestoreComparisonStatus.DIFFERENT, CompareSessions(local, Session(RemoteSavedAt)).Status);
    }

    [Fact]
    public void MessageDifferenceStillBlocks()
    {
        var local = Session(LocalSavedAt);
        local.Message = "Mensaje cambiado";

        Assert.Equal(FirestoreComparisonStatus.DIFFERENT, CompareSessions(local, Session(LocalSavedAt)).Status);
    }

    [Fact]
    public void CommonCcTextDifferenceStillBlocks()
    {
        var local = Session(LocalSavedAt);
        local.CommonCcText = "otro@example.test";

        Assert.Equal(FirestoreComparisonStatus.DIFFERENT, CompareSessions(local, Session(LocalSavedAt)).Status);
    }

    [Fact]
    public void GeneratedPeriodDifferenceStillBlocks()
    {
        var local = Session(LocalSavedAt);
        local.GeneratedPeriod = "2026-09";

        Assert.Equal(FirestoreComparisonStatus.DIFFERENT, CompareSessions(local, Session(LocalSavedAt)).Status);
    }

    [Fact]
    public void BrokerItemDifferenceIsNeverIgnored()
    {
        var local = BrokerItem("Broker cambiado");
        var remote = BrokerItem("Broker original");

        var comparison = FirestoreComparisonService.CompareDocumentFields(
            $"sessions/current/brokerItems/{local.BrokerId:D}",
            new FirestoreBrokerSendItemMapper().ToFields(local),
            new FirestoreBrokerSendItemMapper().ToFields(remote));

        Assert.Equal(FirestoreComparisonStatus.DIFFERENT, comparison.Status);
    }

    [Fact]
    public async Task MissingBrokerItemBlocksCutover()
    {
        using var context = new ComparisonTestContext();
        var item = BrokerItem("Broker");
        var snapshot = Snapshot(Session(LocalSavedAt, item));
        var remote = Documents(snapshot);
        remote.Remove($"sessions/current/brokerItems/{item.BrokerId:D}");

        var report = await RunAsync(context, snapshot, remote, cutoverPreflight: true);
        var execution = CutoverPreflightStartupPolicy.Determine(
            false,
            new FirebaseClientOptions { RequireLegacyCutoverPreflight = true },
            new FirestoreRuntimeState());

        Assert.Equal(1, report.MissingInFirestore);
        Assert.False(report.CanCutOver);
        Assert.Equal(CutoverPreflightExecution.RequiredLegacyTransition, execution);
        Assert.Throws<InvalidOperationException>(() =>
            CutoverPreflightStartupPolicy.DemandLegacyPreflightPassed(
                execution,
                report.CanCutOver,
                report.ReportPath));
    }

    [Fact]
    public async Task All1365DocumentsWithOnlySavedAtDifferenceAreCutoverReady()
    {
        using var context = new ComparisonTestContext();
        var configuration = new AppConfiguration();
        for (var index = 1; index <= 1_363; index++)
        {
            configuration.Brokers.Add(new Broker
            {
                Id = GuidFromIndex(index),
                Name = $"Broker {index}",
                IsActive = true
            });
        }
        var snapshot = new RuntimeApplicationSnapshot(
            configuration,
            Session(LocalSavedAt),
            [],
            [],
            []);
        var remote = Documents(snapshot);
        remote["sessions/current"] = Document(
            "sessions/current",
            new FirestoreCurrentSessionMapper().ToFields(Session(RemoteSavedAt)),
            "session-token");

        var report = await RunAsync(context, snapshot, remote, cutoverPreflight: true);
        var execution = CutoverPreflightStartupPolicy.Determine(
            false,
            new FirebaseClientOptions { RequireLegacyCutoverPreflight = true },
            new FirestoreRuntimeState());

        Assert.Equal(1_365, report.LocalCount);
        Assert.Equal(1_365, report.FirestoreCount);
        Assert.Equal(1_365, report.Identical);
        Assert.Equal(1, report.NonBlockingMetadataDifferences);
        Assert.Equal(["sessions/current.savedAtUtc"], report.NonBlockingMetadataPaths);
        Assert.Equal(0, report.Different);
        Assert.True(report.CanCutOver);
        CutoverPreflightStartupPolicy.DemandLegacyPreflightPassed(
            execution,
            report.CanCutOver,
            report.ReportPath);
    }

    [Fact]
    public async Task ShadowReadUsesTheSameNonBlockingMetadataRule()
    {
        using var context = new ComparisonTestContext();
        var snapshot = Snapshot(Session(LocalSavedAt));
        var remote = Documents(snapshot);
        remote["sessions/current"] = Document(
            "sessions/current",
            new FirestoreCurrentSessionMapper().ToFields(Session(RemoteSavedAt)),
            "session-token");

        var report = await RunAsync(context, snapshot, remote, cutoverPreflight: false);

        Assert.Equal("shadow-read", report.ReportType);
        Assert.Equal(1, report.NonBlockingMetadataDifferences);
        Assert.Equal(0, report.Different);
        Assert.Contains(report.Items, item =>
            item.DocumentPath == "sessions/current" &&
            item.Status == FirestoreComparisonStatus.NON_BLOCKING_METADATA_DIFFERENCE);
    }

    [Fact]
    public async Task FirestorePrimaryStillPersistsSavedAtUtc()
    {
        var result = await ExercisePrimarySessionSaveAsync();

        Assert.Equal("sessions/current", result.Update.DocumentPath);
        Assert.Equal(FirestoreRestValueKind.Timestamp, result.Update.Fields["savedAtUtc"].Kind);
        Assert.Equal(FirestoreTimestampPrecision.Normalize(result.Session.SavedAt),
            result.Update.Fields["savedAtUtc"].RequireTimestamp("savedAtUtc"));
        Assert.True(result.Session.SavedAt > RemoteSavedAt);
    }

    [Fact]
    public async Task FirestorePrimarySessionUpdateStillUsesLoadedUpdateTime()
    {
        var result = await ExercisePrimarySessionSaveAsync();

        Assert.Equal("session-update-token", result.Update.ExpectedUpdateTime);
    }

    [Fact]
    public async Task AuthenticatedCleanInstallationLoadsSharedDataFromFirestorePrimary()
    {
        using var context = new ComparisonTestContext();
        var remoteSnapshot = Snapshot(Session(RemoteSavedAt));
        remoteSnapshot.Configuration.DefaultSubject = "Asunto compartido desde Firestore";
        var client = new InMemoryFirestoreRestClient(Documents(remoteSnapshot));
        var service = new FirestorePrimaryRuntimeDataService(
            client,
            AuthorizedOperator(),
            Snapshot(Session(LocalSavedAt)),
            context.Paths,
            context.Logger);

        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(RuntimeDataMode.FirestorePrimary, service.Mode);
        Assert.Equal("Asunto compartido desde Firestore", loaded.Configuration.DefaultSubject);
    }

    [Fact]
    public async Task FirestorePrimaryDoesNotFallbackToLegacyJsonWhenSharedSettingsAreMissing()
    {
        using var context = new ComparisonTestContext();
        var legacy = Snapshot(Session(LocalSavedAt));
        legacy.Configuration.DefaultSubject = "Asunto legacy que no debe usarse";
        var remote = Documents(legacy);
        remote.Remove("settings/commissions");
        var service = new FirestorePrimaryRuntimeDataService(
            new InMemoryFirestoreRestClient(remote),
            AuthorizedOperator(),
            legacy,
            context.Paths,
            context.Logger);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.LoadAsync(TestContext.Current.CancellationToken));

        Assert.Contains("settings/commissions", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LogoutWithoutLocalChangesDoesNotOverwriteNewerRemoteSession()
    {
        using var context = new ComparisonTestContext();
        var versionA = Snapshot(Session(RemoteSavedAt));
        var documents = Documents(versionA);
        var client = new InMemoryFirestoreRestClient(documents);
        var service = new FirestorePrimaryRuntimeDataService(
            client, AuthorizedOperator(), versionA, context.Paths, context.Logger);
        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
        var versionB = Session(RemoteSavedAt.AddMinutes(1));
        versionB.Subject = "Cambio de PC2";
        documents["sessions/current"] = Document(
            "sessions/current",
            new FirestoreCurrentSessionMapper().ToFields(versionB),
            "pc2-update-token");

        await service.SaveCurrentSessionAsync(
            loaded.CurrentSession!,
            TestContext.Current.CancellationToken);

        Assert.Empty(client.Updates);
        Assert.Equal(
            "Cambio de PC2",
            new FirestoreCurrentSessionMapper().FromFields(documents["sessions/current"].Fields).Subject);
    }

    [Fact]
    public async Task ConcurrentFunctionalSessionChangesRaiseConflictWithoutOverwrite()
    {
        using var context = new ComparisonTestContext();
        var versionA = Snapshot(Session(RemoteSavedAt));
        var documents = Documents(versionA);
        var client = new InMemoryFirestoreRestClient(documents);
        var service = new FirestorePrimaryRuntimeDataService(
            client, AuthorizedOperator(), versionA, context.Paths, context.Logger);
        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
        loaded.CurrentSession!.Subject = "Cambio local de PC1";
        var versionB = Session(RemoteSavedAt.AddMinutes(1));
        versionB.Subject = "Cambio remoto de PC2";
        documents["sessions/current"] = Document(
            "sessions/current",
            new FirestoreCurrentSessionMapper().ToFields(versionB),
            "pc2-update-token");

        await Assert.ThrowsAsync<FirestoreConcurrencyException>(() =>
            service.SaveCurrentSessionAsync(
                loaded.CurrentSession,
                TestContext.Current.CancellationToken));

        Assert.Empty(client.Updates);
        Assert.Equal(
            "Cambio remoto de PC2",
            new FirestoreCurrentSessionMapper().FromFields(documents["sessions/current"].Fields).Subject);
    }

    [Fact]
    public async Task SavedAtOnlyDoesNotCauseSessionWrite()
    {
        using var context = new ComparisonTestContext();
        var snapshot = Snapshot(Session(RemoteSavedAt));
        var client = new InMemoryFirestoreRestClient(Documents(snapshot));
        var service = new FirestorePrimaryRuntimeDataService(
            client, AuthorizedOperator(), snapshot, context.Paths, context.Logger);
        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
        loaded.CurrentSession!.SavedAt = loaded.CurrentSession.SavedAt.AddDays(1);

        await service.SaveCurrentSessionAsync(
            loaded.CurrentSession,
            TestContext.Current.CancellationToken);

        Assert.Empty(client.Updates);
    }

    [Fact]
    public async Task ReloadReadsPc2ChangesWithoutWritingDocuments()
    {
        using var context = new ComparisonTestContext();
        var versionA = Snapshot(Session(RemoteSavedAt));
        var documents = Documents(versionA);
        var client = new InMemoryFirestoreRestClient(documents);
        var service = new FirestorePrimaryRuntimeDataService(
            client, AuthorizedOperator(), versionA, context.Paths, context.Logger);
        _ = await service.LoadAsync(TestContext.Current.CancellationToken);
        var versionB = Snapshot(Session(RemoteSavedAt.AddMinutes(1)));
        versionB.Configuration.DefaultSubject = "Configuración de PC2";
        versionB.CurrentSession!.Message = "Sesión de PC2";
        foreach (var document in Documents(versionB))
        {
            documents[document.Key] = document.Value;
        }

        var reloaded = await service.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Configuración de PC2", reloaded.Configuration.DefaultSubject);
        Assert.Equal("Sesión de PC2", reloaded.CurrentSession!.Message);
        Assert.Empty(client.Updates);
    }

    [Fact]
    public void OtherBusinessTimestampDifferenceRemainsBlocking()
    {
        var left = new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["sentAtUtc"] = FirestoreRestValue.Timestamp(LocalSavedAt)
        };
        var right = new Dictionary<string, FirestoreRestValue>(StringComparer.Ordinal)
        {
            ["sentAtUtc"] = FirestoreRestValue.Timestamp(RemoteSavedAt)
        };

        var comparison = FirestoreComparisonService.CompareDocumentFields("recentSends/r1", left, right);

        Assert.Equal(FirestoreComparisonStatus.DIFFERENT, comparison.Status);
    }

    private static FirestoreDocumentComparison CompareSessions(CurrentSession left, CurrentSession right) =>
        FirestoreComparisonService.CompareDocumentFields(
            "sessions/current",
            new FirestoreCurrentSessionMapper().ToFields(left),
            new FirestoreCurrentSessionMapper().ToFields(right));

    private static CurrentSession Session(DateTimeOffset savedAt, params BrokerSendItem[] items) => new()
    {
        Subject = "Asunto",
        Message = "Mensaje",
        CommonCcText = "cc@example.test",
        GeneratedPeriod = "2026-08",
        SavedAt = savedAt,
        BrokerItems = [.. items]
    };

    private static BrokerSendItem BrokerItem(string name) => new()
    {
        BrokerId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        BrokerName = name,
        Status = SendStatus.Pending
    };

    private static RuntimeApplicationSnapshot Snapshot(CurrentSession session) => new(
        new AppConfiguration(),
        session,
        [],
        [],
        []);

    private static async Task<FirestoreComparisonReport> RunAsync(
        ComparisonTestContext context,
        RuntimeApplicationSnapshot snapshot,
        Dictionary<string, FirestoreRestDocument> remote,
        bool cutoverPreflight) =>
        await new FirestoreComparisonService(
                new InMemoryFirestoreRestClient(remote),
                context.Paths,
                context.Logger)
            .RunAsync(snapshot, cutoverPreflight, TestContext.Current.CancellationToken);

    private static Dictionary<string, FirestoreRestDocument> Documents(RuntimeApplicationSnapshot snapshot)
    {
        var result = new Dictionary<string, FirestoreRestDocument>(StringComparer.Ordinal)
        {
            ["settings/commissions"] = Document(
                "settings/commissions",
                new FirestoreSettingsMapper().ToFields(snapshot.Configuration),
                "settings-token")
        };
        foreach (var broker in snapshot.Configuration.Brokers)
        {
            var path = $"brokers/{broker.Id:D}";
            result[path] = Document(path, new FirestoreBrokerMapper().ToFields(broker), $"broker-{broker.Id:D}");
        }
        if (snapshot.CurrentSession is { } session)
        {
            result["sessions/current"] = Document(
                "sessions/current",
                new FirestoreCurrentSessionMapper().ToFields(session),
                "session-update-token");
            foreach (var item in session.BrokerItems)
            {
                var path = $"sessions/current/brokerItems/{item.BrokerId:D}";
                result[path] = Document(path, new FirestoreBrokerSendItemMapper().ToFields(item), "item-token");
            }
        }
        return result;
    }

    private static FirestoreRestDocument Document(
        string path,
        IReadOnlyDictionary<string, FirestoreRestValue> fields,
        string updateTime) => new()
    {
        Name = $"projects/demo-project/databases/(default)/documents/{path}",
        Fields = fields.ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal),
        UpdateTime = updateTime
    };

    private static Guid GuidFromIndex(int index)
    {
        var bytes = new byte[16];
        BitConverter.GetBytes(index).CopyTo(bytes, 0);
        return new Guid(bytes);
    }

    private static async Task<PrimarySaveResult> ExercisePrimarySessionSaveAsync()
    {
        using var context = new ComparisonTestContext();
        var legacy = Snapshot(Session(RemoteSavedAt));
        var client = new InMemoryFirestoreRestClient(Documents(legacy));
        var service = new FirestorePrimaryRuntimeDataService(
            client,
            new AppUser
            {
                Uid = "operator",
                Email = "operator@example.test",
                DisplayName = "Operator",
                Role = AppUserRole.Operator,
                IsActive = true,
                CanUseCommissions = true
            },
            legacy,
            context.Paths,
            context.Logger);
        var loaded = await service.LoadAsync(TestContext.Current.CancellationToken);
        var session = loaded.CurrentSession!;
        session.Subject = "Cambio funcional local";

        await service.SaveCurrentSessionAsync(session, TestContext.Current.CancellationToken);

        return new PrimarySaveResult(session, Assert.Single(client.Updates));
    }

    private static AppUser AuthorizedOperator() => new()
    {
        Uid = "operator",
        Email = "operator@example.test",
        DisplayName = "Operator",
        Role = AppUserRole.Operator,
        IsActive = true,
        CanUseCommissions = true
    };

    private sealed record PrimarySaveResult(CurrentSession Session, UpdateCall Update);
    private sealed record UpdateCall(
        string DocumentPath,
        IReadOnlyDictionary<string, FirestoreRestValue> Fields,
        string ExpectedUpdateTime);

    private sealed class ComparisonTestContext : IDisposable
    {
        public ComparisonTestContext()
        {
            Root = Path.Combine(Path.GetTempPath(), $"ecs-comparison-{Guid.NewGuid():N}");
            Paths = new AppDataPaths(Root);
            Logger = new FileLogger(Paths);
        }

        public string Root { get; }
        public AppDataPaths Paths { get; }
        public FileLogger Logger { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class InMemoryFirestoreRestClient(
        Dictionary<string, FirestoreRestDocument> documents) : IFirestoreRestClient
    {
        public List<UpdateCall> Updates { get; } = [];

        public Task<FirestoreRestDocument?> GetDocumentAsync(
            string documentPath,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(documents.TryGetValue(documentPath, out var document) ? document : null);
        }

        public Task<IReadOnlyList<FirestoreRestDocument>> ListDocumentsAsync(
            string parentPath,
            string collectionId,
            int pageSize = 200,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var prefix = string.IsNullOrEmpty(parentPath)
                ? $"{collectionId}/"
                : $"{parentPath}/{collectionId}/";
            IReadOnlyList<FirestoreRestDocument> result = documents
                .Where(document => document.Key.StartsWith(prefix, StringComparison.Ordinal) &&
                                   !document.Key[prefix.Length..].Contains('/'))
                .Select(document => document.Value)
                .ToList();
            return Task.FromResult(result);
        }

        public Task<FirestoreRestDocument> CreateDocumentAsync(
            string parentPath,
            string collectionId,
            string documentId,
            IReadOnlyDictionary<string, FirestoreRestValue> fields,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("La prueba no esperaba creates.");

        public Task<FirestoreRestDocument> UpdateDocumentAsync(
            string documentPath,
            IReadOnlyDictionary<string, FirestoreRestValue> fields,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!documents.TryGetValue(documentPath, out var current) ||
                !string.Equals(current.UpdateTime, expectedUpdateTime, StringComparison.Ordinal))
            {
                throw new FirestoreConcurrencyException(documentPath, expectedUpdateTime);
            }
            Updates.Add(new UpdateCall(documentPath, fields, expectedUpdateTime));
            var updated = Document(documentPath, fields, $"updated-{Updates.Count}");
            documents[documentPath] = updated;
            return Task.FromResult(updated);
        }

        public Task DeleteDocumentAsync(
            string documentPath,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("La prueba no esperaba deletes.");
    }
}
