using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public interface IExpirationsBrokerConfigurationService
{
    Task<IReadOnlyList<ExpirationsBrokerConfigurationItem>> ListAsync(
        CancellationToken cancellationToken = default);
    Task<ExpirationsBrokerConfigurationItem?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default);
    Task<ExpirationsBrokerConfigurationSaveResult> SaveAsync(
        ExpirationsBrokerConfigurationItem configuration,
        CancellationToken cancellationToken = default);
}

public sealed class ExpirationsBrokerConfigurationService : IExpirationsBrokerConfigurationService
{
    private readonly IExpirationsBrokerDirectoryRepository _directory;
    private readonly IExpirationsBrokerProfileRepository _profiles;
    private readonly ExpirationsAssistantValidationService _validation;
    private readonly TimeProvider _timeProvider;

    public ExpirationsBrokerConfigurationService(
        IExpirationsBrokerDirectoryRepository directory,
        IExpirationsBrokerProfileRepository profiles,
        ExpirationsAssistantValidationService? validation = null,
        TimeProvider? timeProvider = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _validation = validation ?? new ExpirationsAssistantValidationService();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ExpirationsBrokerConfigurationItem>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        var directoryTask = _directory.ListAsync(cancellationToken);
        var profilesTask = _profiles.ListAsync(cancellationToken);
        await Task.WhenAll(directoryTask, profilesTask);
        var profileDocuments = (await profilesTask)
            .GroupBy(document => document.Value.BrokerId)
            .ToDictionary(group => group.Key, group => group.Last());

        return (await directoryTask)
            .Select(document => Resolve(
                document.Value,
                profileDocuments.GetValueOrDefault(document.Value.BrokerId)))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId)
            .ToList();
    }

    public async Task<ExpirationsBrokerConfigurationItem?> GetAsync(
        Guid brokerId,
        CancellationToken cancellationToken = default)
    {
        var directoryTask = _directory.GetAsync(brokerId, cancellationToken);
        var profileTask = _profiles.GetAsync(brokerId, cancellationToken);
        await Task.WhenAll(directoryTask, profileTask);
        var directory = await directoryTask;
        return directory is null ? null : Resolve(directory.Value, await profileTask);
    }

    public async Task<ExpirationsBrokerConfigurationSaveResult> SaveAsync(
        ExpirationsBrokerConfigurationItem configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var directory = await _directory.GetAsync(configuration.BrokerId, cancellationToken)
            ?? throw new InvalidOperationException("El corredor ya no existe en el maestro compartido.");
        var validation = _validation.ValidateAndNormalize(
            directory.Value.PrimaryEmailAddresses,
            configuration.Assistants);
        var normalizedConfiguration = CopyWithDirectoryIdentity(
            configuration,
            directory.Value,
            validation.Assistants);
        if (!validation.IsValid)
        {
            return new ExpirationsBrokerConfigurationSaveResult
            {
                Outcome = ExpirationsBrokerConfigurationSaveOutcome.ValidationFailed,
                Configuration = normalizedConfiguration,
                Errors = validation.Errors,
                Message = string.Join(Environment.NewLine, validation.Errors)
            };
        }

        if (!Enum.IsDefined(normalizedConfiguration.NextMonthGenerationMode))
        {
            const string error = "El formato de generación del mes siguiente no es válido.";
            return new ExpirationsBrokerConfigurationSaveResult
            {
                Outcome = ExpirationsBrokerConfigurationSaveOutcome.ValidationFailed,
                Configuration = normalizedConfiguration,
                Errors = [error],
                Message = error
            };
        }

        if (normalizedConfiguration.NextMonthGenerationMode ==
            ExpirationsNextMonthGenerationMode.SpecialDualSorted)
        {
            var profiles = await _profiles.ListAsync(cancellationToken);
            if (profiles.Any(document =>
                    document.Value.BrokerId != normalizedConfiguration.BrokerId &&
                    document.Value.NextMonthGenerationMode ==
                    ExpirationsNextMonthGenerationMode.SpecialDualSorted))
            {
                const string error =
                    "Ya existe un corredor configurado con el formato especial de mes siguiente.";
                return new ExpirationsBrokerConfigurationSaveResult
                {
                    Outcome = ExpirationsBrokerConfigurationSaveOutcome.ValidationFailed,
                    Configuration = normalizedConfiguration,
                    Errors = [error],
                    Message = error
                };
            }
        }

        if (!configuration.HasExplicitProfile &&
            configuration.IsActive &&
            validation.Assistants.Count == 0 &&
            normalizedConfiguration.NextMonthGenerationMode ==
            ExpirationsNextMonthGenerationMode.Standard)
        {
            return new ExpirationsBrokerConfigurationSaveResult
            {
                Outcome = ExpirationsBrokerConfigurationSaveOutcome.NoChanges,
                Configuration = normalizedConfiguration,
                Message = "La configuración conserva los valores predeterminados; no fue necesario crear un perfil."
            };
        }

        return configuration.HasExplicitProfile
            ? await UpdateAsync(normalizedConfiguration, validation.Assistants, cancellationToken)
            : await CreateAsync(normalizedConfiguration, validation.Assistants, cancellationToken);
    }

    private async Task<ExpirationsBrokerConfigurationSaveResult> CreateAsync(
        ExpirationsBrokerConfigurationItem configuration,
        IReadOnlyList<ExpirationsAssistant> assistants,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var profile = BuildProfile(configuration, assistants, now, now);
        try
        {
            var stored = await _profiles.CreateAsync(profile, cancellationToken);
            return Success(
                ExpirationsBrokerConfigurationSaveOutcome.Created,
                Resolve(DirectoryFrom(configuration), stored),
                "La configuración de Vencimientos fue guardada.");
        }
        catch (FirestoreRestException ex) when (ex.Kind == FirestoreFailureKind.Conflict)
        {
            return await ConflictAsync(
                configuration.BrokerId,
                "La configuración fue creada o modificada desde otro equipo. Revise los datos antes de guardar nuevamente.",
                cancellationToken);
        }
    }

    private async Task<ExpirationsBrokerConfigurationSaveResult> UpdateAsync(
        ExpirationsBrokerConfigurationItem configuration,
        IReadOnlyList<ExpirationsAssistant> assistants,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.ProfileUpdateTime) ||
            configuration.ProfileCreatedAtUtc is null)
        {
            throw new InvalidOperationException("No se dispone de la versión necesaria para actualizar el perfil.");
        }

        var profile = BuildProfile(
            configuration,
            assistants,
            configuration.ProfileCreatedAtUtc.Value,
            _timeProvider.GetUtcNow());
        try
        {
            var stored = await _profiles.UpdateAsync(
                profile,
                configuration.ProfileUpdateTime,
                cancellationToken);
            return Success(
                ExpirationsBrokerConfigurationSaveOutcome.Updated,
                Resolve(DirectoryFrom(configuration), stored),
                "La configuración de Vencimientos fue actualizada.");
        }
        catch (FirestoreConcurrencyException)
        {
            return await ConflictAsync(
                configuration.BrokerId,
                "La configuración cambió desde otro equipo. Se recargaron los datos para evitar sobrescribir cambios.",
                cancellationToken);
        }
    }

    private async Task<ExpirationsBrokerConfigurationSaveResult> ConflictAsync(
        Guid brokerId,
        string message,
        CancellationToken cancellationToken)
    {
        var reloaded = await GetAsync(brokerId, cancellationToken)
            ?? throw new InvalidOperationException("El corredor ya no existe en el maestro compartido.");
        return new ExpirationsBrokerConfigurationSaveResult
        {
            Outcome = ExpirationsBrokerConfigurationSaveOutcome.ConcurrencyConflict,
            Configuration = reloaded,
            Message = message
        };
    }

    private static ExpirationsBrokerConfigurationSaveResult Success(
        ExpirationsBrokerConfigurationSaveOutcome outcome,
        ExpirationsBrokerConfigurationItem configuration,
        string message) => new()
    {
        Outcome = outcome,
        Configuration = configuration,
        Message = message
    };

    private static ExpirationsBrokerProfile BuildProfile(
        ExpirationsBrokerConfigurationItem configuration,
        IReadOnlyList<ExpirationsAssistant> assistants,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc) => new()
    {
        BrokerId = configuration.BrokerId,
        IsActive = configuration.IsActive,
        NextMonthGenerationMode = configuration.NextMonthGenerationMode,
        Assistants = assistants.Select(CopyAssistant).ToList(),
        CreatedAtUtc = createdAtUtc,
        UpdatedAtUtc = updatedAtUtc
    };

    private static ExpirationsBrokerConfigurationItem Resolve(
        ExpirationsBrokerDirectoryEntry directory,
        FirestoreStoredDocument<ExpirationsBrokerProfile>? profileDocument)
    {
        var profile = profileDocument?.Value;
        return new ExpirationsBrokerConfigurationItem
        {
            BrokerId = directory.BrokerId,
            Name = directory.Name,
            PrimaryEmailAddresses = directory.PrimaryEmailAddresses.ToList(),
            IsActive = profile?.IsActive ?? true,
            NextMonthGenerationMode = profile?.NextMonthGenerationMode ??
                                      ExpirationsNextMonthGenerationMode.Standard,
            Assistants = (profile?.Assistants ?? [])
                .Select(CopyAssistant)
                .OrderBy(assistant => assistant.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(assistant => assistant.Email, StringComparer.OrdinalIgnoreCase)
                .ThenBy(assistant => assistant.Id)
                .ToList(),
            HasExplicitProfile = profileDocument is not null,
            ProfileUpdateTime = profileDocument?.UpdateTime,
            ProfileCreatedAtUtc = profile?.CreatedAtUtc,
            ProfileUpdatedAtUtc = profile?.UpdatedAtUtc
        };
    }

    private static ExpirationsBrokerConfigurationItem CopyWithDirectoryIdentity(
        ExpirationsBrokerConfigurationItem source,
        ExpirationsBrokerDirectoryEntry directory,
        IReadOnlyList<ExpirationsAssistant> assistants) => new()
    {
        BrokerId = directory.BrokerId,
        Name = directory.Name,
        PrimaryEmailAddresses = directory.PrimaryEmailAddresses.ToList(),
        IsActive = source.IsActive,
        NextMonthGenerationMode = source.NextMonthGenerationMode,
        Assistants = assistants.Select(CopyAssistant).ToList(),
        HasExplicitProfile = source.HasExplicitProfile,
        ProfileUpdateTime = source.ProfileUpdateTime,
        ProfileCreatedAtUtc = source.ProfileCreatedAtUtc,
        ProfileUpdatedAtUtc = source.ProfileUpdatedAtUtc
    };

    private static ExpirationsBrokerDirectoryEntry DirectoryFrom(
        ExpirationsBrokerConfigurationItem configuration) => new()
    {
        BrokerId = configuration.BrokerId,
        Name = configuration.Name,
        PrimaryEmailAddresses = configuration.PrimaryEmailAddresses.ToList()
    };

    private static ExpirationsAssistant CopyAssistant(ExpirationsAssistant source) => new()
    {
        Id = source.Id,
        Name = source.Name,
        Email = source.Email,
        IsActive = source.IsActive
    };
}
