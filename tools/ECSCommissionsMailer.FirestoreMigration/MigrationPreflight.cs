using Google.Cloud.Firestore;

namespace ECSCommissionsMailer.FirestoreMigration;

public sealed record ExistingFirestoreDocument(
    string Path,
    IReadOnlyDictionary<string, object?> Fields);

public sealed record DocumentMismatch(string Path, string State, IReadOnlyList<string> Differences);

public sealed class MigrationPreflightResult
{
    public List<string> ToCreate { get; } = [];
    public List<string> Identical { get; } = [];
    public List<DocumentMismatch> Conflicts { get; } = [];
    public int ExistingDocumentCount { get; set; }
}

public static class MigrationPreflightAnalyzer
{
    public static MigrationPreflightResult Analyze(
        IEnumerable<PlannedFirestoreDocument> planned,
        IEnumerable<ExistingFirestoreDocument> existing)
    {
        var result = new MigrationPreflightResult();
        var expectedByPath = planned.ToDictionary(
            value => FirestoreDocumentPath.Normalize(value.Path),
            StringComparer.Ordinal);
        var actualByPath = existing.ToDictionary(
            value => FirestoreDocumentPath.Normalize(value.Path),
            value => new ExistingFirestoreDocument(
                FirestoreDocumentPath.Normalize(value.Path),
                value.Fields),
            StringComparer.Ordinal);
        result.ExistingDocumentCount = actualByPath.Count;

        foreach (var (canonicalPath, expected) in expectedByPath)
        {
            if (!actualByPath.TryGetValue(canonicalPath, out var actual))
            {
                result.ToCreate.Add(canonicalPath);
                continue;
            }

            var forbidden = LocalOnlyFieldGuard.FindForbiddenFields(actual.Fields);
            var differences = FirestoreSemanticComparer.Compare(expected.ExpectedFields, actual.Fields).ToList();
            differences.AddRange(forbidden.Select(path => $"{path}: campo LOCAL_ONLY prohibido"));
            if (differences.Count == 0)
            {
                result.Identical.Add(canonicalPath);
            }
            else
            {
                result.Conflicts.Add(new DocumentMismatch(canonicalPath, "DIFFERENT", differences));
            }
        }

        foreach (var extra in actualByPath.Keys.Except(expectedByPath.Keys, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
        {
            var differences = new List<string> { "Documento EXTRA en Firestore sin equivalente en los JSON productivos." };
            differences.AddRange(LocalOnlyFieldGuard.FindForbiddenFields(actualByPath[extra].Fields)
                .Select(path => $"{path}: campo LOCAL_ONLY prohibido"));
            result.Conflicts.Add(new DocumentMismatch(extra, "EXTRA", differences));
        }

        result.ToCreate.Sort(StringComparer.Ordinal);
        result.Identical.Sort(StringComparer.Ordinal);
        return result;
    }

    public static IReadOnlyList<DocumentMismatch> ToVerificationMismatches(MigrationPreflightResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Conflicts
            .Concat(result.ToCreate.Select(path =>
                new DocumentMismatch(path, "MISSING", ["Documento ausente en Firestore."])))
            .ToList();
    }
}

public sealed class FirestoreMigrationStore(FirestoreDb database)
{
    private readonly FirestoreDb _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<IReadOnlyList<ExistingFirestoreDocument>> ReadOperationalDocumentsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ExistingFirestoreDocument>(StringComparer.Ordinal);
        foreach (var collection in new[] { "settings", "brokers", "sessions", "recentSends", "paymentGenerations" })
        {
            var snapshot = await _database.Collection(collection).GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            Add(snapshot, result);
        }

        var brokerItems = await _database.CollectionGroup("brokerItems").GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        Add(brokerItems, result);
        var generationFiles = await _database.CollectionGroup("files").GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        Add(generationFiles, result);
        return result.Values.OrderBy(value => value.Path, StringComparer.Ordinal).ToList();
    }

    public async Task<(bool SchemaExists, bool MigrationStateExists)> ReadBootstrapStateAsync(
        CancellationToken cancellationToken = default)
    {
        var schema = await _database.Document("system/schema").GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var state = await _database.Document("system/migrationState").GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return (schema.Exists, state.Exists);
    }

    public async Task SetMigrationInProgressAsync(
        string manifestSha256,
        int sourceSchemaVersion,
        CancellationToken cancellationToken = default)
    {
        await _database.Document(MigrationStateUpdates.DocumentPath).SetAsync(
            new Dictionary<string, object>
            {
                ["source"] = "local-json",
                ["status"] = "in-progress",
                ["dataMigrated"] = false,
                ["startedAtUtc"] = FieldValue.ServerTimestamp,
                ["sourceManifestSha256"] = manifestSha256,
                ["sourceSchemaVersion"] = (long)sourceSchemaVersion,
                ["migrationToolVersion"] = MigrationConstants.ToolVersion
            },
            SetOptions.MergeAll,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMigrationCompletedAsync(MigrationCounts counts, CancellationToken cancellationToken = default)
    {
        await _database.Document(MigrationStateUpdates.DocumentPath).SetAsync(
            MigrationStateUpdates.Completed(counts),
            SetOptions.MergeAll,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task SetMigrationFailedAsync(string summary, CancellationToken cancellationToken = default)
    {
        await _database.Document(MigrationStateUpdates.DocumentPath).SetAsync(
            MigrationStateUpdates.Failed(summary),
            SetOptions.MergeAll,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CreateOrConfirmIdenticalAsync(
        PlannedFirestoreDocument planned,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await planned.CreateAsync(_database.Document(planned.Path), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Grpc.Core.RpcException exception) when (exception.StatusCode == Grpc.Core.StatusCode.AlreadyExists)
        {
            var snapshot = await _database.Document(planned.Path).GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var actual = ToNullableDictionary(snapshot.ToDictionary());
            var differences = FirestoreSemanticComparer.Compare(planned.ExpectedFields, actual);
            if (differences.Count == 0)
            {
                return false;
            }

            throw new InvalidOperationException(
                $"CONFLICT durante el reintento de {planned.Path}: {string.Join("; ", differences.Take(5))}");
        }
    }

    private static void Add(QuerySnapshot snapshot, IDictionary<string, ExistingFirestoreDocument> target)
    {
        foreach (var document in snapshot.Documents)
        {
            // Google.Cloud.Firestore 4.3.0 defines DocumentReference.Path as the
            // complete resource name, including project and database ID.
            var canonicalPath = FirestoreDocumentPath.Normalize(document.Reference.Path);
            target[canonicalPath] = new ExistingFirestoreDocument(
                canonicalPath,
                ToNullableDictionary(document.ToDictionary()));
        }
    }

    private static IReadOnlyDictionary<string, object?> ToNullableDictionary(IDictionary<string, object> source) =>
        source.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
}
