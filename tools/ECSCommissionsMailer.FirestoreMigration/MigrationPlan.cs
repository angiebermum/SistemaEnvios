using ECS.CommissionsMailer.Infrastructure.Firestore;
using ECS.CommissionsMailer.Infrastructure.Firestore.Converters;
using ECS.CommissionsMailer.Infrastructure.Firestore.Mapping;
using Google.Cloud.Firestore;

namespace ECSCommissionsMailer.FirestoreMigration;

public enum MigrationDocumentCategory
{
    Settings,
    Broker,
    PaymentGeneration,
    PaymentGenerationFile,
    Session,
    SessionBrokerItem,
    RecentSend
}

public sealed class PlannedFirestoreDocument
{
    private readonly Func<DocumentReference, CancellationToken, Task> _create;

    private PlannedFirestoreDocument(
        string path,
        MigrationDocumentCategory category,
        object value,
        Func<DocumentReference, CancellationToken, Task> create)
    {
        Path = path;
        Category = category;
        Value = value;
        ExpectedFields = FirestoreDocumentProjection.Project(value);
        _create = create;
    }

    public string Path { get; }
    public MigrationDocumentCategory Category { get; }
    public object Value { get; }
    public IReadOnlyDictionary<string, object?> ExpectedFields { get; }

    public Task CreateAsync(DocumentReference reference, CancellationToken cancellationToken) =>
        _create(reference, cancellationToken);

    public static PlannedFirestoreDocument Create<T>(string path, MigrationDocumentCategory category, T value)
        where T : class
    {
        LocalOnlyFieldGuard.ThrowIfForbiddenFieldsExist(FirestoreDocumentProjection.Project(value));
        return new PlannedFirestoreDocument(
            FirestoreDocumentPath.Normalize(path),
            category,
            value,
            async (reference, cancellationToken) =>
            {
                await reference.CreateAsync(value, cancellationToken).ConfigureAwait(false);
            });
    }
}

public sealed record MigrationCounts(
    int Settings,
    int Brokers,
    int Session,
    int SessionBrokerItems,
    int RecentSends,
    int PaymentGenerations,
    int PaymentGenerationFiles);

public sealed class MigrationPlan
{
    public List<PlannedFirestoreDocument> Documents { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public Dictionary<string, int> LocalOnlyExcluded { get; } = new(StringComparer.Ordinal);

    public MigrationCounts Counts => new(
        Documents.Count(value => value.Category == MigrationDocumentCategory.Settings),
        Documents.Count(value => value.Category == MigrationDocumentCategory.Broker),
        Documents.Count(value => value.Category == MigrationDocumentCategory.Session),
        Documents.Count(value => value.Category == MigrationDocumentCategory.SessionBrokerItem),
        Documents.Count(value => value.Category == MigrationDocumentCategory.RecentSend),
        Documents.Count(value => value.Category == MigrationDocumentCategory.PaymentGeneration),
        Documents.Count(value => value.Category == MigrationDocumentCategory.PaymentGenerationFile));
}

public static class MigrationPlanBuilder
{
    public static MigrationPlan Build(MigrationSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var plan = new MigrationPlan();
        plan.Errors.AddRange(source.Errors);
        plan.Warnings.AddRange(source.Warnings);

        ValidateSource(source, plan);
        if (plan.Errors.Count > 0)
        {
            return plan;
        }

        try
        {
            var configuration = FirestorePersistenceMapper.Partition(source.Configuration);
            Add(plan, "settings/commissions", MigrationDocumentCategory.Settings, configuration.SharedSettings);
            foreach (var broker in configuration.SharedBrokers)
            {
                Add(plan, $"brokers/{FirestorePaths.GuidDocumentId(broker.Id)}", MigrationDocumentCategory.Broker, broker);
            }

            var generationPartitions = source.PaymentGenerations.Select(generation => new
            {
                Generation = generation,
                Partition = FirestorePersistenceMapper.Partition(
                    generation,
                    file => DeterministicDocumentIds.ForGenerationFile(generation.Id, file))
            }).ToList();
            foreach (var entry in generationPartitions)
            {
                Add(plan,
                    $"paymentGenerations/{FirestorePaths.GuidDocumentId(entry.Generation.Id)}",
                    MigrationDocumentCategory.PaymentGeneration,
                    entry.Partition.SharedGeneration);
            }

            foreach (var entry in generationPartitions)
            {
                foreach (var file in entry.Partition.SharedFiles)
                {
                    Add(plan,
                        $"paymentGenerations/{FirestorePaths.GuidDocumentId(entry.Generation.Id)}/files/{FirestorePaths.GuidDocumentId(file.Id)}",
                        MigrationDocumentCategory.PaymentGenerationFile,
                        file);
                }
            }

            var session = FirestorePersistenceMapper.Partition(source.Session);
            Add(plan, "sessions/current", MigrationDocumentCategory.Session, session.SharedSession);
            foreach (var item in session.SharedBrokerItems)
            {
                Add(plan,
                    $"sessions/current/brokerItems/{FirestorePaths.GuidDocumentId(item.BrokerId)}",
                    MigrationDocumentCategory.SessionBrokerItem,
                    item);
            }

            foreach (var loadedSend in source.RecentSends)
            {
                var send = loadedSend.Value;
                if (!loadedSend.HadStableId)
                {
                    send.Id = DeterministicDocumentIds.ForRecentSend(send);
                }

                var partition = FirestorePersistenceMapper.Partition(send);
                Add(plan,
                    $"recentSends/{FirestorePaths.GuidDocumentId(send.Id)}",
                    MigrationDocumentCategory.RecentSend,
                    partition.Shared);
            }

            CountLocalOnly(source, plan);
            ValidateUniqueDocumentPaths(plan);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            plan.Errors.Add($"No fue posible mapear los documentos SHARED: {exception.Message}");
        }

        return plan;
    }

    private static void ValidateSource(MigrationSource source, MigrationPlan plan)
    {
        var brokers = source.Configuration.Brokers ?? [];
        ValidateUniqueNonEmptyIds(brokers.Select(value => value.Id), "Broker.Id", plan);
        foreach (var broker in brokers)
        {
            ValidateUniqueNonEmptyIds((broker.Assistants ?? []).Select(value => value.Id), $"Broker {broker.Id}.Assistants.Id", plan);
            ValidateUniqueNonEmptyIds((broker.Deductions ?? []).Select(value => value.Id), $"Broker {broker.Id}.Deductions.Id", plan);
            foreach (var deduction in broker.Deductions ?? [])
            {
                ValidateDecimal(deduction.Amount, $"Broker {broker.Id}.Deduction {deduction.Id}.Amount", plan);
            }
        }

        var brokerIds = brokers.Select(value => value.Id).ToHashSet();
        var sessionItems = source.Session.BrokerItems ?? [];
        ValidateUniqueNonEmptyIds(sessionItems.Select(value => value.BrokerId), "Session.BrokerItems.BrokerId", plan);
        foreach (var item in sessionItems.Where(item => !brokerIds.Contains(item.BrokerId)))
        {
            plan.Errors.Add($"BrokerItem {item.BrokerId} referencia un Broker inexistente.");
        }
        foreach (var item in sessionItems)
        {
            ValidateUniqueNonEmptyIds(
                (item.Assistants ?? []).Select(value => value.Id),
                $"Session BrokerItem {item.BrokerId}.Assistants.Id",
                plan);
        }

        ValidateUniqueNonEmptyIds(source.PaymentGenerations.Select(value => value.Id), "PaymentGeneration.Id", plan);
        var generationIds = source.PaymentGenerations.Select(value => value.Id).ToHashSet();
        if (source.Session.ActivePaymentGenerationId is { } active && !generationIds.Contains(active))
        {
            plan.Errors.Add($"ActivePaymentGenerationId {active} no corresponde a una generación que se migrará.");
        }

        foreach (var generation in source.PaymentGenerations)
        {
            foreach (var file in generation.Files ?? [])
            {
                if (file.BrokerId == Guid.Empty)
                {
                    plan.Errors.Add($"Generation {generation.Id} contiene un archivo con BrokerId vacío.");
                }
                else if (!brokerIds.Contains(file.BrokerId))
                {
                    plan.Errors.Add($"Generation {generation.Id} contiene un archivo para Broker inexistente {file.BrokerId}.");
                }

                ValidateCalculation(file.Crc, $"Generation {generation.Id}/{file.WorksheetName}/CRC", plan);
                ValidateCalculation(file.Usd, $"Generation {generation.Id}/{file.WorksheetName}/USD", plan);
            }
        }

        foreach (var loaded in source.RecentSends)
        {
            if (loaded.Value.BrokerId == Guid.Empty)
            {
                plan.Errors.Add("Un RecentSend contiene BrokerId vacío.");
            }

            if (loaded.Value.PaymentGenerationId is { } generationId && !generationIds.Contains(generationId))
            {
                plan.Errors.Add($"RecentSend referencia la generación inexistente {generationId}.");
            }
        }

        ValidateDecimalSamples(plan);
    }

    private static void ValidateCalculation(
        ECS.CommissionsMailer.Models.PaymentCurrencyCalculation? calculation,
        string path,
        MigrationPlan plan)
    {
        if (calculation is null)
        {
            plan.Errors.Add($"{path}: falta el snapshot financiero.");
            return;
        }

        var values = new[]
        {
            calculation.MinimumAmount,
            calculation.GrossCommissionOriginal,
            calculation.GrossDeductions,
            calculation.AdjustedGrossCommission,
            calculation.Vat,
            calculation.InvoiceAmount,
            calculation.Withholding,
            calculation.PayableBeforeFinalDeductions,
            calculation.FinalDeductions,
            calculation.DepositedAmount
        }.Concat((calculation.Deductions ?? []).SelectMany(value => new[] { value.ConfiguredAmount, value.AppliedAmount }));

        foreach (var value in values)
        {
            ValidateDecimal(value, path, plan);
        }

        ValidateUniqueNonEmptyIds(
            (calculation.Deductions ?? []).Select(value => value.Id),
            $"{path}.Deductions.Id",
            plan);
    }

    private static void ValidateDecimalSamples(MigrationPlan plan)
    {
        var converter = new DecimalStringConverter();
        foreach (var value in new[] { 15000m, 3589.15m, 214336.00m, 615524.44m })
        {
            var roundTrip = converter.FromFirestore(converter.ToFirestore(value));
            if (!decimal.GetBits(value).SequenceEqual(decimal.GetBits(roundTrip)))
            {
                plan.Errors.Add($"El conversor decimal falló para {value}.");
            }
        }
    }

    private static void ValidateDecimal(decimal value, string path, MigrationPlan plan)
    {
        var converter = new DecimalStringConverter();
        var roundTrip = converter.FromFirestore(converter.ToFirestore(value));
        if (!decimal.GetBits(value).SequenceEqual(decimal.GetBits(roundTrip)))
        {
            plan.Errors.Add($"{path}: el decimal {value} no conserva un round-trip exacto.");
        }
    }

    private static void ValidateUniqueNonEmptyIds(IEnumerable<Guid> ids, string name, MigrationPlan plan)
    {
        var seen = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (id == Guid.Empty)
            {
                plan.Errors.Add($"{name} contiene un UUID vacío.");
            }
            else if (!seen.Add(id))
            {
                plan.Errors.Add($"{name} contiene el UUID duplicado {id}.");
            }
        }
    }

    private static void Add<T>(MigrationPlan plan, string path, MigrationDocumentCategory category, T value)
        where T : class => plan.Documents.Add(PlannedFirestoreDocument.Create(path, category, value));

    private static void ValidateUniqueDocumentPaths(MigrationPlan plan)
    {
        foreach (var duplicate in plan.Documents.GroupBy(value => value.Path, StringComparer.Ordinal).Where(value => value.Count() > 1))
        {
            plan.Errors.Add($"Dos datos locales producen el mismo DocumentId: {duplicate.Key}.");
        }
    }

    private static void CountLocalOnly(MigrationSource source, MigrationPlan plan)
    {
        plan.LocalOnlyExcluded["SignatureImagePath"] = 1;
        plan.LocalOnlyExcluded["GeneralWorkbookPath"] = 1;
        plan.LocalOnlyExcluded["GeneratedOutputDirectory"] = 1;
        plan.LocalOnlyExcluded["AttachmentPaths"] = (source.Session.BrokerItems ?? []).Count;
        plan.LocalOnlyExcluded["GeneratedAttachmentPaths"] = (source.Session.BrokerItems ?? []).Count;
        plan.LocalOnlyExcluded["ArchivedAttachmentPaths"] = source.RecentSends.Count;
        plan.LocalOnlyExcluded["SourceWorkbookPath"] = source.PaymentGenerations.Count;
        plan.LocalOnlyExcluded["OutputDirectory"] = source.PaymentGenerations.Count;
        plan.LocalOnlyExcluded["OutputPath"] = source.PaymentGenerations.Sum(value => (value.Files ?? []).Count);
    }
}
