using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsRoutingAdministrationService
{
    Task<IReadOnlyList<ExpirationsAssociationAdministrationItem>> ListAssociationsAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExpirationsExclusionAdministrationItem>> ListExclusionsAsync(
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
    Task<ExpirationsRoutingAdministrationResult> SetExclusionActiveAsync(
        Guid exclusionId,
        bool isActive,
        string expectedUpdateTime,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsRoutingAdministrationService : IExpirationsRoutingAdministrationService
{
    private readonly IExpirationsBrokerAssociationRepository _associations;
    private readonly IExpirationsExclusionRepository _exclusions;
    private readonly IExpirationsBrokerConfigurationService _brokers;
    private readonly ExpirationsBrokerNormalizer _normalizer;
    private readonly TimeProvider _timeProvider;

    public ExpirationsRoutingAdministrationService(
        IExpirationsBrokerAssociationRepository associations,
        IExpirationsExclusionRepository exclusions,
        IExpirationsBrokerConfigurationService brokers,
        ExpirationsBrokerNormalizer? normalizer = null,
        TimeProvider? timeProvider = null)
    {
        _associations = associations ?? throw new ArgumentNullException(nameof(associations));
        _exclusions = exclusions ?? throw new ArgumentNullException(nameof(exclusions));
        _brokers = brokers ?? throw new ArgumentNullException(nameof(brokers));
        _normalizer = normalizer ?? new ExpirationsBrokerNormalizer();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

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
        return await UpdateAssociationAsync(updated, expectedUpdateTime, cancellationToken);
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
        if (candidate.IsActive)
        {
            var conflict = await ValidateAssociationActivationAsync(candidate, associationId, cancellationToken);
            if (conflict is not null)
                return conflict;
        }

        candidate.UpdatedAtUtc = _timeProvider.GetUtcNow();
        return await UpdateAssociationAsync(candidate, expectedUpdateTime, cancellationToken);
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
            return Result(
                ExpirationsRoutingAdministrationOutcome.Conflict,
                "Ya existe otra asociación activa para el mismo valor. Corrija el conflicto antes de continuar.");
        }
        if ((await exclusionsTask).Any(document =>
                document.Value.IsActive &&
                Normalize(document.Value.NormalizedValue, document.Value.Value) == normalized))
        {
            return Result(
                ExpirationsRoutingAdministrationOutcome.Conflict,
                "Este valor está marcado como No distribuir. Desactive o corrija la exclusión primero.");
        }
        return null;
    }

    private async Task<ExpirationsRoutingAdministrationResult> UpdateAssociationAsync(
        ExpirationsBrokerAssociation association,
        string expectedUpdateTime,
        CancellationToken cancellationToken)
    {
        try
        {
            await _associations.UpdateAsync(association, expectedUpdateTime, cancellationToken);
            return Result(
                ExpirationsRoutingAdministrationOutcome.Updated,
                association.IsActive ? "La asociación fue actualizada." : "La asociación fue desactivada.");
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
