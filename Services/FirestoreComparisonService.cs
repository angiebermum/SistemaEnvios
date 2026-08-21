using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Authorization;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Models;

namespace ECS.CommissionsMailer.Services;

public enum FirestoreComparisonStatus
{
    MATCH,
    NON_BLOCKING_METADATA_DIFFERENCE,
    MISSING_IN_FIRESTORE,
    MISSING_LOCALLY,
    DIFFERENT
}

public sealed record FirestoreComparisonItem(string DocumentPath, FirestoreComparisonStatus Status)
{
    public List<string> NonBlockingMetadataFields { get; init; } = [];
}

internal sealed record FirestoreDocumentComparison(
    FirestoreComparisonStatus Status,
    IReadOnlyList<string> NonBlockingMetadataFields);

public sealed class FirestoreComparisonReport
{
    public string ReportType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public int LocalCount { get; set; }
    public int FirestoreCount { get; set; }
    public int Identical { get; set; }
    public int MissingInFirestore { get; set; }
    public int MissingLocally { get; set; }
    public int Different { get; set; }
    public int NonBlockingMetadataDifferences { get; set; }
    public List<string> NonBlockingMetadataPaths { get; set; } = [];
    public int Conflicts => Different;
    public bool CanCutOver => MissingInFirestore == 0 && MissingLocally == 0 && Different == 0;
    public List<FirestoreComparisonItem> Items { get; set; } = [];
    public string ReportPath { get; set; } = string.Empty;
}

internal sealed class FirestoreComparisonService(
    IFirestoreRestClient client,
    AppDataPaths paths,
    FileLogger logger)
{
    public async Task<FirestoreComparisonReport> RunAsync(
        RuntimeApplicationSnapshot local,
        bool cutoverPreflight,
        CancellationToken cancellationToken = default)
    {
        var localDocuments = BuildLocalDocuments(local);
        var remoteDocuments = await LoadRemoteDocumentsAsync(cancellationToken);
        var allPaths = localDocuments.Keys.Concat(remoteDocuments.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToList();
        var report = new FirestoreComparisonReport
        {
            ReportType = cutoverPreflight ? "cutover-preflight" : "shadow-read",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            LocalCount = localDocuments.Count,
            FirestoreCount = remoteDocuments.Count
        };
        foreach (var path in allPaths)
        {
            var hasLocal = localDocuments.TryGetValue(path, out var localFields);
            var hasRemote = remoteDocuments.TryGetValue(path, out var remoteFields);
            var comparison = (hasLocal, hasRemote) switch
            {
                (true, false) => new FirestoreDocumentComparison(
                    FirestoreComparisonStatus.MISSING_IN_FIRESTORE, []),
                (false, true) => new FirestoreDocumentComparison(
                    FirestoreComparisonStatus.MISSING_LOCALLY, []),
                _ => CompareDocumentFields(path, localFields!, remoteFields!)
            };
            report.Items.Add(new FirestoreComparisonItem(path, comparison.Status)
            {
                NonBlockingMetadataFields = [.. comparison.NonBlockingMetadataFields]
            });
            switch (comparison.Status)
            {
                case FirestoreComparisonStatus.MATCH: report.Identical++; break;
                case FirestoreComparisonStatus.NON_BLOCKING_METADATA_DIFFERENCE:
                    report.Identical++;
                    report.NonBlockingMetadataDifferences += comparison.NonBlockingMetadataFields.Count;
                    report.NonBlockingMetadataPaths.AddRange(
                        comparison.NonBlockingMetadataFields.Select(field => $"{path}.{field}"));
                    break;
                case FirestoreComparisonStatus.MISSING_IN_FIRESTORE: report.MissingInFirestore++; break;
                case FirestoreComparisonStatus.MISSING_LOCALLY: report.MissingLocally++; break;
                case FirestoreComparisonStatus.DIFFERENT: report.Different++; break;
            }
        }

        var directory = cutoverPreflight
            ? paths.FirestoreCutoverReportsDirectory
            : paths.FirestoreShadowReportsDirectory;
        Directory.CreateDirectory(directory);
        report.ReportPath = Path.Combine(
            directory,
            $"{report.ReportType}-{DateTime.Now:yyyyMMdd-HHmmssfff}.json");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        });
        await File.WriteAllTextAsync(report.ReportPath, json, cancellationToken);
        logger.Info(
            $"{report.ReportType}: local={report.LocalCount}; firestore={report.FirestoreCount}; " +
            $"identical={report.Identical}; missingCloud={report.MissingInFirestore}; " +
            $"missingLocal={report.MissingLocally}; different={report.Different}; " +
            $"nonBlockingMetadata={report.NonBlockingMetadataDifferences}.");
        return report;
    }

    private static Dictionary<string, IReadOnlyDictionary<string, FirestoreRestValue>> BuildLocalDocuments(
        RuntimeApplicationSnapshot snapshot)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, FirestoreRestValue>>(StringComparer.Ordinal)
        {
            ["settings/commissions"] = new FirestoreSettingsMapper().ToFields(snapshot.Configuration)
        };
        var brokerMapper = new FirestoreBrokerMapper();
        foreach (var broker in snapshot.Configuration.Brokers)
            result[$"brokers/{broker.Id:D}"] = brokerMapper.ToFields(broker);

        if (snapshot.CurrentSession is { } session)
        {
            result["sessions/current"] = new FirestoreCurrentSessionMapper().ToFields(session);
            var itemMapper = new FirestoreBrokerSendItemMapper();
            foreach (var item in session.BrokerItems)
                result[$"sessions/current/brokerItems/{item.BrokerId:D}"] = itemMapper.ToFields(item);
        }

        var recentMapper = new FirestoreRecentSendMapper();
        foreach (var send in snapshot.RecentSends)
        {
            if (send.Id == Guid.Empty) send.Id = RuntimeDeterministicDocumentIds.ForRecentSend(send);
            result[$"recentSends/{send.Id:D}"] = recentMapper.ToFields(send);
        }

        var generationMapper = new FirestorePaymentGenerationMapper();
        var fileMapper = new FirestorePaymentGenerationFileMapper();
        foreach (var generation in snapshot.PaymentGenerations)
        {
            result[$"paymentGenerations/{generation.Id:D}"] = generationMapper.ToFields(generation);
            foreach (var file in generation.Files)
            {
                var fileId = RuntimeDeterministicDocumentIds.ForGenerationFile(generation.Id, file);
                result[$"paymentGenerations/{generation.Id:D}/files/{fileId:D}"] =
                    fileMapper.ToFields(new FirestoreGenerationFile(fileId, file));
            }
        }
        return result;
    }

    private async Task<Dictionary<string, IReadOnlyDictionary<string, FirestoreRestValue>>> LoadRemoteDocumentsAsync(
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<string, FirestoreRestValue>>(StringComparer.Ordinal);
        var settingsTask = client.GetDocumentAsync("settings/commissions", cancellationToken);
        var sessionTask = client.GetDocumentAsync("sessions/current", cancellationToken);
        var brokersTask = client.ListDocumentsAsync(string.Empty, "brokers", cancellationToken: cancellationToken);
        var itemsTask = client.ListDocumentsAsync("sessions/current", "brokerItems", cancellationToken: cancellationToken);
        var recentTask = client.ListDocumentsAsync(string.Empty, "recentSends", cancellationToken: cancellationToken);
        var generationsTask = client.ListDocumentsAsync(string.Empty, "paymentGenerations", cancellationToken: cancellationToken);
        await Task.WhenAll(settingsTask, sessionTask, brokersTask, itemsTask, recentTask, generationsTask);

        Add(result, await settingsTask);
        Add(result, await sessionTask);
        foreach (var document in await brokersTask) Add(result, document);
        foreach (var document in await itemsTask) Add(result, document);
        foreach (var document in await recentTask) Add(result, document);
        var generations = await generationsTask;
        foreach (var document in generations) Add(result, document);

        var fileTasks = generations.Select(document => client.ListDocumentsAsync(
            $"paymentGenerations/{document.DocumentId}",
            "files",
            cancellationToken: cancellationToken)).ToList();
        var files = await Task.WhenAll(fileTasks);
        foreach (var group in files)
            foreach (var document in group) Add(result, document);
        return result;
    }

    private static void Add(
        IDictionary<string, IReadOnlyDictionary<string, FirestoreRestValue>> target,
        FirestoreRestDocument? document)
    {
        if (document is null) return;
        var marker = "/documents/";
        var index = document.Name.IndexOf(marker, StringComparison.Ordinal);
        var path = index < 0 ? document.Name : document.Name[(index + marker.Length)..];
        target[path] = document.Fields;
    }

    internal static FirestoreDocumentComparison CompareDocumentFields(
        string documentPath,
        IReadOnlyDictionary<string, FirestoreRestValue> left,
        IReadOnlyDictionary<string, FirestoreRestValue> right)
    {
        if (Equal(documentPath, left, right))
            return new FirestoreDocumentComparison(FirestoreComparisonStatus.MATCH, []);

        // savedAtUtc is persistence/autosave metadata only for the session header.
        // It remains persisted normally, but must not block ShadowRead or Cutover Preflight.
        if (string.Equals(documentPath, "sessions/current", StringComparison.Ordinal) &&
            Equal(
                documentPath,
                WithoutField(left, "savedAtUtc"),
                WithoutField(right, "savedAtUtc")))
        {
            return new FirestoreDocumentComparison(
                FirestoreComparisonStatus.NON_BLOCKING_METADATA_DIFFERENCE,
                ["savedAtUtc"]);
        }

        return new FirestoreDocumentComparison(FirestoreComparisonStatus.DIFFERENT, []);
    }

    private static bool Equal(
        string documentPath,
        IReadOnlyDictionary<string, FirestoreRestValue> left,
        IReadOnlyDictionary<string, FirestoreRestValue> right) =>
        string.Equals(
            CanonicalFields(left, documentPath),
            CanonicalFields(right, documentPath),
            StringComparison.Ordinal);

    private static IReadOnlyDictionary<string, FirestoreRestValue> WithoutField(
        IReadOnlyDictionary<string, FirestoreRestValue> fields,
        string fieldName) => fields
        .Where(field => !string.Equals(field.Key, fieldName, StringComparison.Ordinal))
        .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);

    internal static string CanonicalFields(
        IReadOnlyDictionary<string, FirestoreRestValue> fields,
        string documentPath = "<sin ruta>")
    {
        var value = FirestoreRestValue.Map(fields.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal));
        var builder = new StringBuilder();
        Append(builder, value, documentPath, "$fields");
        return builder.ToString();
    }

    private static void Append(
        StringBuilder builder,
        FirestoreRestValue? value,
        string documentPath,
        string fieldPath)
    {
        if (value is null)
            throw InvalidValue(documentPath, fieldPath, "sin value_type");

        switch (value.Kind)
        {
            case FirestoreRestValueKind.Null:
                builder.Append("null;");
                return;
            case FirestoreRestValueKind.String:
                if (value.StringValue is not { } text)
                    throw InvalidValue(documentPath, fieldPath, value.RestTypeName);
                AppendText(builder, 's', text);
                return;
            case FirestoreRestValueKind.Boolean:
                if (!value.BooleanValue.HasValue)
                    throw InvalidValue(documentPath, fieldPath, value.RestTypeName);
                builder.Append(value.BooleanValue.Value ? "b1;" : "b0;");
                return;
            case FirestoreRestValueKind.Integer:
                if (!long.TryParse(value.IntegerValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
                    throw InvalidValue(documentPath, fieldPath, value.RestTypeName);
                builder.Append('i').Append(integer).Append(';');
                return;
            case FirestoreRestValueKind.Timestamp:
                if (!DateTimeOffset.TryParse(
                        value.TimestampValue,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var timestamp))
                    throw InvalidValue(documentPath, fieldPath, value.RestTypeName);
                AppendText(builder, 't', FirestoreTimestampPrecision.Normalize(timestamp)
                    .ToString("O", CultureInfo.InvariantCulture));
                return;
            case FirestoreRestValueKind.Array:
                if (value.ArrayValue is not { } array)
                    throw InvalidValue(documentPath, fieldPath, value.RestTypeName);
                builder.Append('[');
                for (var index = 0; index < array.Values.Count; index++)
                    Append(builder, array.Values[index], documentPath, $"{fieldPath}[{index}]");
                builder.Append("];");
                return;
            case FirestoreRestValueKind.Map:
                if (value.MapValue is not { } map)
                    throw InvalidValue(documentPath, fieldPath, value.RestTypeName);
                builder.Append('{');
                foreach (var field in map.Fields.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    AppendText(builder, 'k', field.Key);
                    Append(builder, field.Value, documentPath, $"{fieldPath}.{field.Key}");
                }
                builder.Append("};");
                return;
            default:
                throw InvalidValue(documentPath, fieldPath, $"Kind={(int)value.Kind}");
        }
    }

    private static InvalidDataException InvalidValue(string documentPath, string fieldPath, string restType) =>
        new($"Firestore Value inválido en documento '{documentPath}', campo '{fieldPath}', tipo REST '{restType}'.");

    private static void AppendText(StringBuilder builder, char type, string value) =>
        builder.Append(type).Append(value.Length).Append(':').Append(value).Append(';');
}

internal sealed class ShadowReadRuntimeDataService(
    JsonOnlyRuntimeDataService json,
    FirestoreComparisonService comparison,
    AppUser currentUser) : IRuntimeDataService
{
    private RuntimeApplicationSnapshot? _lastSnapshot;

    public RuntimeDataMode Mode => RuntimeDataMode.FirestoreShadowRead;
    public AppUser? CurrentUser { get; } = currentUser;
    public FirestoreComparisonReport? LastReport { get; private set; }

    public async Task<RuntimeApplicationSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        _lastSnapshot = await json.LoadAsync(cancellationToken);
        LastReport = await comparison.RunAsync(_lastSnapshot, false, cancellationToken);
        return _lastSnapshot with
        {
            Warnings = _lastSnapshot.Warnings.Concat(new[]
            {
                $"ShadowRead: MATCH={LastReport.Identical}; MISSING_IN_FIRESTORE={LastReport.MissingInFirestore}; " +
                $"MISSING_LOCALLY={LastReport.MissingLocally}; DIFFERENT={LastReport.Different}; " +
                $"NON_BLOCKING_METADATA_DIFFERENCES={LastReport.NonBlockingMetadataDifferences}. " +
                $"Reporte: {LastReport.ReportPath}"
            }).ToList()
        };
    }

    public Task<AppConfiguration> ReloadConfigurationAsync(CancellationToken cancellationToken = default) =>
        json.ReloadConfigurationAsync(cancellationToken);
    public Task SaveConfigurationAsync(AppConfiguration configuration, CancellationToken cancellationToken = default) =>
        json.SaveConfigurationAsync(configuration, cancellationToken);
    public Task SaveCurrentSessionAsync(CurrentSession session, CancellationToken cancellationToken = default) =>
        json.SaveCurrentSessionAsync(session, cancellationToken);
    public Task SaveRecentSendsAsync(IEnumerable<SentEmailRecord> records, CancellationToken cancellationToken = default) =>
        json.SaveRecentSendsAsync(records, cancellationToken);
    public Task SavePaymentGenerationsAsync(IEnumerable<PaymentGenerationBatch> batches, CancellationToken cancellationToken = default) =>
        json.SavePaymentGenerationsAsync(batches, cancellationToken);
}
