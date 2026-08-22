using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using System.Text.RegularExpressions;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsRoutingAdministrationService
{
    Task<IReadOnlyList<ExpirationsAssociationAdministrationItem>> ListAssociationsAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExpirationsKnownIdentifierAdministrationItem>> ListKnownIdentifiersAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExpirationsExclusionAdministrationItem>> ListExclusionsAsync(
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> CreateAssociationAsync(
        Guid brokerId,
        ExpirationsAssociationKind kind,
        string value,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> EditAssociationAsync(
        Guid associationId,
        ExpirationsAssociationKind kind,
        string value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> SetAssociationActiveAsync(
        Guid associationId,
        bool isActive,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> ReassignAsync(
        Guid associationId,
        Guid destinationBrokerId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> DeleteAssociationAsync(
        Guid associationId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> ConfirmObservedIdentifierAsync(
        Guid identifierId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> ReassignAndConfirmObservedIdentifierAsync(
        Guid identifierId,
        Guid destinationBrokerId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> IgnoreObservedIdentifierAsync(
        Guid identifierId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
    Task<ExpirationsRoutingAdministrationResult> SetExclusionActiveAsync(
        Guid exclusionId,
        bool isActive,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsRoutingAdministrationService : IExpirationsRoutingAdministrationService
{
    private static readonly Regex StrictCodePattern = new(
        @"(?:^|/)\s*(?<letters>[A-Za-z]{2,10})\s*-\s*(?<digits>[0-9]{1,6})\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IExpirationsBrokerAssociationRepository _associations;
    private readonly IExpirationsExclusionRepository _exclusions;
    private readonly IExpirationsBrokerConfigurationService _brokers;
    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;
    private readonly IExpirationsObservedIdentifierRepository? _observedIdentifiers;

    public ExpirationsRoutingAdministrationService(
        IExpirationsBrokerAssociationRepository associations,
        IExpirationsExclusionRepository exclusions,
        IExpirationsBrokerConfigurationService brokers,
        ExpirationsBrokerNormalizer? normalizer = null,
        TimeProvider? timeProvider = null,
        IExpirationsObservedIdentifierRepository? observedIdentifiers = null)
    {
        _associations = associations ?? throw new ArgumentNullException(nameof(associations));
        _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
        _brokers = brokers ?? throw new ArgumentNullException(nameof(brokers));
        _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observedIdentifiers = observedIdentifiers;
    }

    public async Task<IReadOnlyList<ExpirationsKnownIdentifierAdministrationItem>> ListKnownIdentifiersAsync(
        CancellationToken cancellationToken = default)
    {
        var associationsTask = ListAssociationsAsync(cancellationToken);
        var brokersTask = _brokers.ListAsync(cancellationToken);
        var observedTask = _observedIdentifiers?.ListAsync(cancellationToken) ??
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsObservedIdentifier>>>([]);
        await Task.WhenAll(associationsTask, brokersTask, observedTask);
        var brokers = (await brokersTask).ToDictionary(item => item.BrokerId);
        var associationItems = await associationsTask;
        var observedDocuments = await observedTask;
        var associationNormalizedValues = associationItems
            .Select(item => Normalize(item.Association.NormalizedValue, item.Association.Value))
            .ToHashSet(StringComparer.Ordinal);
        var masterNormalizedValues = brokers.Values
            .Select(item => _normalizer.Normalize(item.Name))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var result = new List<ExpirationsKnownIdentifierAdministrationItem>();

        foreach (var broker in brokers.Values)
        {
            result.Add(new ExpirationsKnownIdentifierAdministrationItem
            {
                BrokerId = broker.BrokerId,
                BrokerName = broker.Name,
                BrokerPrimaryEmail = broker.PrimaryEmailAddresses.FirstOrDefault(email =>
                    !string.IsNullOrWhiteSpace(email)) ?? string.Empty,
                Kind = ExpirationsAssociationKind.Name,
                Value = broker.Name,
                NormalizedValue = _normalizer.Normalize(broker.Name),
                OriginText = "Maestro",
                StatusText = broker.IsActive ? "En uso" : "Inactivo",
                UpdatedAtUtc = broker.ProfileUpdatedAtUtc,
                IsMaster = true
            });
        }

        result.AddRange(associationItems
            .Where(item => !masterNormalizedValues.Contains(
                Normalize(item.Association.NormalizedValue, item.Association.Value)))
            .Select(item => new ExpirationsKnownIdentifierAdministrationItem
            {
                BrokerId = item.Association.BrokerId,
                BrokerName = item.BrokerName,
                BrokerPrimaryEmail = item.BrokerPrimaryEmail,
                Kind = item.Association.Kind,
                Value = item.Association.Value,
                NormalizedValue = Normalize(item.Association.NormalizedValue, item.Association.Value),
                OriginText = item.OriginText,
                StatusText = item.Association.IsActive ? "En uso" : "Inactivo",
                UpdatedAtUtc = item.Association.UpdatedAtUtc,
                AssociationItem = item
            }));

        var observedPairs = BuildObservedPairs(observedDocuments);
        foreach (var document in observedDocuments)
        {
            var observed = document.Value;
            if (observedPairs.PairedAliasIds.Contains(observed.Id))
                continue;
            var normalized = Normalize(observed.NormalizedValue, observed.Value);
            if (observed.IsIgnored || associationNormalizedValues.Contains(normalized) ||
                masterNormalizedValues.Contains(normalized))
                continue;
            brokers.TryGetValue(observed.BrokerId, out var broker);
            var item = new ExpirationsObservedIdentifierAdministrationItem
            {
                Identifier = ExpirationsObservedIdentifierCaptureService.Copy(observed),
                UpdateTime = document.UpdateTime,
                BrokerName = broker?.Name ?? $"Corredor inexistente ({observed.BrokerId:D})",
                BrokerPrimaryEmail = broker?.PrimaryEmailAddresses.FirstOrDefault(email =>
                    !string.IsNullOrWhiteSpace(email)) ?? string.Empty
            };
            result.Add(new ExpirationsKnownIdentifierAdministrationItem
            {
                BrokerId = observed.BrokerId,
                BrokerName = item.BrokerName,
                BrokerPrimaryEmail = item.BrokerPrimaryEmail,
                Kind = observed.Kind,
                Value = observed.Value,
                NormalizedValue = normalized,
                OriginText = "Detectado automáticamente",
                StatusText = "Detectado",
                DetailText = observedPairs.AliasByCodeId.TryGetValue(observed.Id, out var pairedAlias)
                    ? $"Visto en el Excel como: {pairedAlias.Value.Value}"
                    : string.Empty,
                UpdatedAtUtc = observedPairs.AliasByCodeId.TryGetValue(observed.Id, out pairedAlias)
                    ? new[] { observed.LastSeenAtUtc, pairedAlias.Value.LastSeenAtUtc }.Max()
                    : observed.LastSeenAtUtc,
                ObservedItem = item
            });
        }

        return result
            .OrderBy(item => item.BrokerName, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(item => item.IsMaster)
            .ThenBy(item => item.Value, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.NormalizedValue, StringComparer.Ordinal)
            .ToList();
    }

    private ObservedPairs BuildObservedPairs(
        IReadOnlyList<FirestoreStoredDocument<ExpirationsObservedIdentifier>> documents)
    {
        var codes = documents
            .Where(document => document.Value.Kind == ExpirationsAssociationKind.Code)
            .GroupBy(document => (
                document.Value.BrokerId,
                NormalizedValue: Normalize(document.Value.NormalizedValue, document.Value.Value)))
            .ToDictionary(group => group.Key, group => group.ToList());
        var aliases = documents
            .Where(document => document.Value.Kind == ExpirationsAssociationKind.Alias)
            .Select(document => (Document: document, Code: ExtractStrictCode(document.Value.Value)))
            .Where(item => item.Code is not null)
            .GroupBy(item => (item.Document.Value.BrokerId, NormalizedValue: item.Code!))
            .ToDictionary(group => group.Key, group => group.Select(item => item.Document).ToList());
        var aliasByCodeId = new Dictionary<Guid, FirestoreStoredDocument<ExpirationsObservedIdentifier>>();
        var pairedAliasIds = new HashSet<Guid>();
        foreach (var pair in codes)
        {
            if (pair.Value.Count != 1 || !aliases.TryGetValue(pair.Key, out var matchingAliases) ||
                matchingAliases.Count != 1)
            {
                continue;
            }
            aliasByCodeId[pair.Value[0].Value.Id] = matchingAliases[0];
            pairedAliasIds.Add(matchingAliases[0].Value.Id);
        }
        return new ObservedPairs(aliasByCodeId, pairedAliasIds);
    }

    private string? ExtractStrictCode(string value)
    {
        var match = StrictCodePattern.Match(value.Trim());
        if (!match.Success)
            return null;
        var code = $"{match.Groups["letters"].Value.ToUpperInvariant()} - {match.Groups["digits"].Value}";
        return _normalizer.Normalize(code);
    }

    private sealed record ObservedPairs(
        IReadOnlyDictionary<Guid, FirestoreStoredDocument<ExpirationsObservedIdentifier>> AliasByCodeId,
        IReadOnlySet<Guid> PairedAliasIds);

    public async Task<IReadOnlyList<ExpirationsAssociationAdministrationItem>> ListAssociationsAsync(
        CancellationToken cancellationToken = default)
    {
        var associationsTask = _associations.ListAsync(cancellationToken);
        var brokersTask = _brokers.ListAsync(cancellationToken);
        await Task.WhenAll(associationsTask, brokersTask);
        var brokers = (await brokersTask).ToDictionary(item => item.BrokerId);
        return (await associationsTask)
            .Select(document =>
            {
                brokers.TryGetValue(document.Value.BrokerId, out var broker);
                return new ExpirationsAssociationAdministrationItem
                {
                    Association = Copy(document.Value),
                    UpdateTime = document.UpdateTime,
                    BrokerName = broker?.Name ?? $"Corredor inexistente ({document.Value.BrokerId:D})",
                    BrokerPrimaryEmail = broker?.PrimaryEmailAddresses.FirstOrDefault(email =>
                        !string.IsNullOrWhiteSpace(email)) ?? string.Empty
                };
            })
            .OrderBy(item => item.Association.Value, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.Association.Id)
            .ToList();
    }

    public async Task<IReadOnlyList<ExpirationsExclusionAdministrationItem>> ListExclusionsAsync(
        CancellationToken cancellationToken = default) =>
        (await _exclusions.ListAsync(cancellationToken))
        .Select(document => new ExpirationsExclusionAdministrationItem
        {
            Exclusion = Copy(document.Value),
            UpdateTime = document.UpdateTime
        })
        .OrderBy(item => item.Exclusion.Value, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.Exclusion.Id)
        .ToList();

    public async Task<ExpirationsRoutingAdministrationResult> CreateAssociationAsync(
        Guid brokerId,
        ExpirationsAssociationKind kind,
        string value,
        CancellationToken cancellationToken = default) =>
        await CreateAssociationCoreAsync(
            brokerId,
            kind,
            value,
            ExpirationsAssociationOrigin.ManuallyAdded,
            cancellationToken);

    private async Task<ExpirationsRoutingAdministrationResult> CreateAssociationCoreAsync(
        Guid brokerId,
        ExpirationsAssociationKind kind,
        string value,
        ExpirationsAssociationOrigin origin,
        CancellationToken cancellationToken)
    {
        var validation = ValidateAssociationInput(kind, value);
        if (validation is not null)
            return validation;
        var broker = await _brokers.GetAsync(brokerId, cancellationToken);
        if (broker is null || !broker.IsActive)
        {
            return Result(
                ExpirationsRoutingAdministrationOutcome.Rejected,
                "El corredor seleccionado no existe o está inactivo en Vencimientos.");
        }

        var now = _timeProvider.GetUtcNow();
        var association = new ExpirationsBrokerAssociation
        {
            Id = Guid.NewGuid(),
            BrokerId = brokerId,
            Kind = kind,
            Value = value.Trim(),
            NormalizedValue = _normalizer.Normalize(value),
            Origin = origin,
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        if (association.NormalizedValue.Length == 0)
            return Result(ExpirationsRoutingAdministrationOutcome.Rejected, "Digite un valor identificable.");
        var conflict = await ValidateAssociationActivationAsync(association, association.Id, cancellationToken);
        if (conflict is not null)
            return conflict;

        try
        {
            await _associations.CreateAsync(association, cancellationToken);
            return Result(
                ExpirationsRoutingAdministrationOutcome.Updated,
                "La asociación fue guardada.");
        }
        catch (FirestoreRestException ex) when (ex.Kind == FirestoreFailureKind.Conflict)
        {
            return Result(
                ExpirationsRoutingAdministrationOutcome.Conflict,
                "La asociación fue creada desde otro equipo. Se recargarán los datos.");
        }
    }

    public async Task<ExpirationsRoutingAdministrationResult> EditAssociationAsync(
        Guid associationId,
        ExpirationsAssociationKind kind,
        string value,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var validation = ValidateAssociationInput(kind, value);
        if (validation is not null)
            return validation;
        var stored = await _associations.GetAsync(associationId, cancellationToken);
        if (stored is null)
            return Result(ExpirationsRoutingAdministrationOutcome.NotFound, "La asociación ya no existe.");
        if (!string.Equals(stored.UpdateTime, expectedUpdateTime, StringComparison.Ordinal))
            return Concurrency();

        var candidate = Copy(stored.Value);
        candidate.Kind = kind;
        candidate.Value = value.Trim();
        candidate.NormalizedValue = _normalizer.Normalize(value);
        if (candidate.NormalizedValue.Length == 0)
            return Result(ExpirationsRoutingAdministrationOutcome.Rejected, "Digite un valor identificable.");
        var conflict = await ValidateAssociationActivationAsync(candidate, associationId, cancellationToken);
        if (conflict is not null)
            return conflict;
        candidate.UpdatedAtUtc = _timeProvider.GetUtcNow();
        return await UpdateAssociationAsync(
            candidate,
            expectedUpdateTime,
            "La asociación fue actualizada.",
            cancellationToken);
    }

    public async Task<ExpirationsRoutingAdministrationResult> SetAssociationActiveAsync(
        Guid associationId,
        bool isActive,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var stored = await _associations.GetAsync(associationId, cancellationToken);
        if (stored is null)
            return Result(ExpirationsRoutingAdministrationOutcome.NotFound, "La asociación ya no existe.");
        if (!string.Equals(stored.UpdateTime, expectedUpdateTime, StringComparison.Ordinal))
            return Concurrency();
        if (stored.Value.IsActive == isActive)
            return Result(ExpirationsRoutingAdministrationOutcome.Rejected, "La asociación ya tiene ese estado.");

        if (isActive)
        {
            var conflict = await ValidateAssociationActivationAsync(stored.Value, associationId, cancellationToken);
            if (conflict is not null)
                return conflict;
        }

        var updated = Copy(stored.Value);
        updated.IsActive = isActive;
        updated.UpdatedAtUtc = _timeProvider.GetUtcNow();
        return await UpdateAssociationAsync(
            updated,
            expectedUpdateTime,
            isActive ? "La asociación fue reactivada." : "La asociación fue inactivada.",
            cancellationToken);
    }

    public async Task<ExpirationsRoutingAdministrationResult> ReassignAsync(
        Guid associationId,
        Guid destinationBrokerId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var storedTask = _associations.GetAsync(associationId, cancellationToken);
        var brokerTask = _brokers.GetAsync(destinationBrokerId, cancellationToken);
        await Task.WhenAll(storedTask, brokerTask);
        var stored = await storedTask;
        var destination = await brokerTask;
        if (stored is null)
            return Result(ExpirationsRoutingAdministrationOutcome.NotFound, "La asociación ya no existe.");
        if (!string.Equals(stored.UpdateTime, expectedUpdateTime, StringComparison.Ordinal))
            return Concurrency();
        if (destination is null || !destination.IsActive)
            return Result(
                ExpirationsRoutingAdministrationOutcome.Rejected,
                "El corredor de destino no existe o está inactivo en Vencimientos.");
        if (stored.Value.BrokerId == destinationBrokerId)
            return Result(ExpirationsRoutingAdministrationOutcome.Rejected, "La asociación ya pertenece a ese corredor.");

        var candidate = Copy(stored.Value);
        candidate.BrokerId = destinationBrokerId;
        var conflict = await ValidateAssociationActivationAsync(candidate, associationId, cancellationToken);
        if (conflict is not null)
            return conflict;

        candidate.UpdatedAtUtc = _timeProvider.GetUtcNow();
        return await UpdateAssociationAsync(
            candidate,
            expectedUpdateTime,
            "La asociación fue reasignada.",
            cancellationToken);
    }

    public async Task<ExpirationsRoutingAdministrationResult> DeleteAssociationAsync(
        Guid associationId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var stored = await _associations.GetAsync(associationId, cancellationToken);
        if (stored is null)
            return Result(ExpirationsRoutingAdministrationOutcome.NotFound, "La asociación ya no existe.");
        if (!string.Equals(stored.UpdateTime, expectedUpdateTime, StringComparison.Ordinal))
            return Concurrency();
        try
        {
            await _associations.DeleteAsync(associationId, expectedUpdateTime, cancellationToken);
            return Result(
                ExpirationsRoutingAdministrationOutcome.Updated,
                "La asociación fue eliminada.");
        }
        catch (FirestoreConcurrencyException)
        {
            return Concurrency();
        }
    }

    public async Task<ExpirationsRoutingAdministrationResult> ConfirmObservedIdentifierAsync(
        Guid identifierId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var observed = await GetObservedAsync(identifierId, expectedUpdateTime, cancellationToken);
        if (observed.Result is not null)
            return observed.Result;
        return await CreateAssociationCoreAsync(
            observed.Document!.Value.BrokerId,
            observed.Document.Value.Kind,
            observed.Document.Value.Value,
            ExpirationsAssociationOrigin.ManuallyConfirmed,
            cancellationToken);
    }

    public async Task<ExpirationsRoutingAdministrationResult> ReassignAndConfirmObservedIdentifierAsync(
        Guid identifierId,
        Guid destinationBrokerId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var observed = await GetObservedAsync(identifierId, expectedUpdateTime, cancellationToken);
        if (observed.Result is not null)
            return observed.Result;
        var created = await CreateAssociationCoreAsync(
            destinationBrokerId,
            observed.Document!.Value.Kind,
            observed.Document.Value.Value,
            ExpirationsAssociationOrigin.ManuallyConfirmed,
            cancellationToken);
        if (!created.WasPersisted)
            return created;
        return await IgnoreObservedCoreAsync(
            observed.Document,
            "El identificador fue reasignado y confirmado.",
            cancellationToken);
    }

    public async Task<ExpirationsRoutingAdministrationResult> IgnoreObservedIdentifierAsync(
        Guid identifierId,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var observed = await GetObservedAsync(identifierId, expectedUpdateTime, cancellationToken);
        if (observed.Result is not null)
            return observed.Result;
        return await IgnoreObservedCoreAsync(
            observed.Document!,
            "El identificador observado fue ignorado.",
            cancellationToken);
    }

    public async Task<ExpirationsRoutingAdministrationResult> SetExclusionActiveAsync(
        Guid exclusionId,
        bool isActive,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default)
    {
        var stored = await _exclusions.GetAsync(exclusionId, cancellationToken);
        if (stored is null)
            return Result(ExpirationsRoutingAdministrationOutcome.NotFound, "La exclusión ya no existe.");
        if (!string.Equals(stored.UpdateTime, expectedUpdateTime, StringComparison.Ordinal))
            return Concurrency();
        if (stored.Value.IsActive == isActive)
            return Result(ExpirationsRoutingAdministrationOutcome.Rejected, "La exclusión ya tiene ese estado.");
        if (isActive)
        {
            var normalized = Normalize(stored.Value.NormalizedValue, stored.Value.Value);
            var associations = await _associations.ListAsync(cancellationToken);
            var conflict = associations.FirstOrDefault(document =>
                document.Value.IsActive && Normalize(
                    document.Value.NormalizedValue,
                    document.Value.Value) == normalized);
            if (conflict is not null)
            {
                var broker = await _brokers.GetAsync(conflict.Value.BrokerId, cancellationToken);
                return Result(
                    ExpirationsRoutingAdministrationOutcome.Conflict,
                    $"Este valor ya está asociado a {broker?.Name ?? conflict.Value.BrokerId.ToString("D")}. " +
                    "Desactive o corrija esa asociación primero.");
            }
        }

        var updated = Copy(stored.Value);
        updated.IsActive = isActive;
        updated.UpdatedAtUtc = _timeProvider.GetUtcNow();
        try
        {
            await _exclusions.UpdateAsync(updated, expectedUpdateTime, cancellationToken);
            return Result(
                ExpirationsRoutingAdministrationOutcome.Updated,
                isActive ? "La exclusión fue reactivada." : "La exclusión fue desactivada.");
        }
        catch (FirestoreConcurrencyException)
        {
            return Concurrency();
        }
    }

    private async Task<ExpirationsRoutingAdministrationResult?> ValidateAssociationActivationAsync(
        ExpirationsBrokerAssociation candidate,
        Guid associationId,
        CancellationToken cancellationToken)
    {
        var normalized = Normalize(candidate.NormalizedValue, candidate.Value);
        var associationsTask = _associations.ListAsync(cancellationToken);
        var exclusionsTask = _exclusions.ListAsync(cancellationToken);
        await Task.WhenAll(associationsTask, exclusionsTask);
        var associationConflict = (await associationsTask).FirstOrDefault(document =>
            document.Value.Id != associationId &&
            document.Value.IsActive &&
            Normalize(document.Value.NormalizedValue, document.Value.Value) == normalized);
        if (associationConflict is not null)
        {
            var broker = await _brokers.GetAsync(associationConflict.Value.BrokerId, cancellationToken);
            return Result(
                ExpirationsRoutingAdministrationOutcome.Conflict,
                $"{candidate.Value.Trim()} ya está asociado a " +
                $"{broker?.Name ?? associationConflict.Value.BrokerId.ToString("D")}.");
        }
        if ((await exclusionsTask).Any(document =>
                document.Value.IsActive &&
                Normalize(document.Value.NormalizedValue, document.Value.Value) == normalized))
        {
            return Result(
                ExpirationsRoutingAdministrationOutcome.Conflict,
                $"{candidate.Value.Trim()} está excluido. Desactive la exclusión antes de asociarlo.");
        }
        return null;
    }

    private async Task<(
        FirestoreStoredDocument<ExpirationsObservedIdentifier>? Document,
        ExpirationsRoutingAdministrationResult? Result)> GetObservedAsync(
        Guid identifierId,
        string expectedUpdateTime,
        CancellationToken cancellationToken)
    {
        if (_observedIdentifiers is null)
        {
            return (null, Result(
                ExpirationsRoutingAdministrationOutcome.Rejected,
                "La administración de identificadores observados no está disponible."));
        }
        var stored = await _observedIdentifiers.GetAsync(identifierId, cancellationToken);
        if (stored is null)
            return (null, Result(ExpirationsRoutingAdministrationOutcome.NotFound,
                "El identificador observado ya no existe."));
        if (!string.Equals(stored.UpdateTime, expectedUpdateTime, StringComparison.Ordinal))
            return (null, Concurrency());
        if (stored.Value.IsIgnored)
            return (null, Result(ExpirationsRoutingAdministrationOutcome.Rejected,
                "El identificador observado ya fue ignorado."));
        return (stored, null);
    }

    private async Task<ExpirationsRoutingAdministrationResult> IgnoreObservedCoreAsync(
        FirestoreStoredDocument<ExpirationsObservedIdentifier> stored,
        string successMessage,
        CancellationToken cancellationToken)
    {
        var updated = ExpirationsObservedIdentifierCaptureService.Copy(stored.Value);
        updated.IsIgnored = true;
        try
        {
            await _observedIdentifiers!.UpdateAsync(updated, stored.UpdateTime, cancellationToken);
            return Result(ExpirationsRoutingAdministrationOutcome.Updated, successMessage);
        }
        catch (FirestoreConcurrencyException)
        {
            var latest = await _observedIdentifiers!.GetAsync(stored.Value.Id, cancellationToken);
            if (latest is null || latest.Value.IsIgnored)
                return Result(ExpirationsRoutingAdministrationOutcome.Updated, successMessage);
            updated = ExpirationsObservedIdentifierCaptureService.Copy(latest.Value);
            updated.IsIgnored = true;
            try
            {
                await _observedIdentifiers.UpdateAsync(updated, latest.UpdateTime, cancellationToken);
                return Result(ExpirationsRoutingAdministrationOutcome.Updated, successMessage);
            }
            catch (FirestoreConcurrencyException)
            {
                return Concurrency();
            }
        }
    }

    private static ExpirationsRoutingAdministrationResult? ValidateAssociationInput(
        ExpirationsAssociationKind kind,
        string value)
    {
        if (!Enum.IsDefined(kind))
        {
            return Result(
                ExpirationsRoutingAdministrationOutcome.Rejected,
                "Seleccione un tipo de asociación válido.");
        }
        if (string.IsNullOrWhiteSpace(value))
        {
            return Result(
                ExpirationsRoutingAdministrationOutcome.Rejected,
                "Digite el valor que identifica al corredor.");
        }
        return null;
    }

    private async Task<ExpirationsRoutingAdministrationResult> UpdateAssociationAsync(
        ExpirationsBrokerAssociation association,
        string expectedUpdateTime,
        string successMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await _associations.UpdateAsync(association, expectedUpdateTime, cancellationToken);
            return Result(
                ExpirationsRoutingAdministrationOutcome.Updated,
                successMessage);
        }
        catch (FirestoreConcurrencyException)
        {
            return Concurrency();
        }
    }

    private string Normalize(string normalizedValue, string value) =>
        _normalizer.Normalize(normalizedValue.Length > 0 ? normalizedValue : value);

    private static ExpirationsRoutingAdministrationResult Concurrency() =>
        Result(
            ExpirationsRoutingAdministrationOutcome.ConcurrencyConflict,
            "La información cambió desde otro equipo. Se recargaron los datos para evitar sobrescribir cambios.");

    private static ExpirationsRoutingAdministrationResult Result(
        ExpirationsRoutingAdministrationOutcome outcome,
        string message) => new() { Outcome = outcome, Message = message };

    private static ExpirationsBrokerAssociation Copy(ExpirationsBrokerAssociation value) => new()
    {
        Id = value.Id,
        BrokerId = value.BrokerId,
        Kind = value.Kind,
        Value = value.Value,
        NormalizedValue = value.NormalizedValue,
        Origin = value.Origin,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc
    };

    private static ExpirationsExclusion Copy(ExpirationsExclusion value) => new()
    {
        Id = value.Id,
        Value = value.Value,
        NormalizedValue = value.NormalizedValue,
        IsActive = value.IsActive,
        CreatedAtUtc = value.CreatedAtUtc,
        UpdatedAtUtc = value.UpdatedAtUtc
    };
}
