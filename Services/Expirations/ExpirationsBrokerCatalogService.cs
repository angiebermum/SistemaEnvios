using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

public sealed class ExpirationsBrokerCatalogService(
    IExpirationsBrokerDirectoryRepository directory,
    IExpirationsBrokerProfileRepository profiles,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ExpirationsBrokerCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var directoryTask = directory.ListAsync(cancellationToken);
        var profilesTask = profiles.ListAsync(cancellationToken);
        await Task.WhenAll(directoryTask, profilesTask);

        var brokerEntries = await directoryTask;
        var profileDocuments = (await profilesTask).ToList();
        var felixDirectory = brokerEntries
            .Select(document => document.Value)
            .FirstOrDefault(value => value.BrokerId == ExpirationsBrokerProfileDefaults.FelixLaraBrokerId);
        var felixProfile = await ExpirationsBrokerProfileDefaults.EnsureAsync(
            felixDirectory,
            profileDocuments.FirstOrDefault(document =>
                document.Value.BrokerId == ExpirationsBrokerProfileDefaults.FelixLaraBrokerId),
            profiles,
            _timeProvider,
            cancellationToken);
        if (felixProfile is not null && profileDocuments.All(document =>
                document.Value.BrokerId != ExpirationsBrokerProfileDefaults.FelixLaraBrokerId))
            profileDocuments.Add(felixProfile);
        var profilesByBroker = profileDocuments
            .Select(document => document.Value)
            .GroupBy(profile => profile.BrokerId)
            .ToDictionary(group => group.Key, group => group.Last());
        var brokerIds = brokerEntries.Select(document => document.Value.BrokerId).ToHashSet();
        var warnings = profilesByBroker.Keys
            .Where(brokerId => !brokerIds.Contains(brokerId))
            .OrderBy(brokerId => brokerId)
            .Select(brokerId =>
                $"Se ignoró el perfil de Vencimientos huérfano para el corredor {brokerId:D}.")
            .ToList();

        var items = brokerEntries
            .Select(document => Resolve(document.Value, profilesByBroker.GetValueOrDefault(document.Value.BrokerId)))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(item => item.BrokerId)
            .ToList();
        return new ExpirationsBrokerCatalog(items, warnings);
    }

    private static ExpirationsBrokerCatalogItem Resolve(
        ExpirationsBrokerDirectoryEntry broker,
        ExpirationsBrokerProfile? profile) => new()
    {
        BrokerId = broker.BrokerId,
        Name = broker.Name,
        PrimaryEmailAddresses = [.. broker.PrimaryEmailAddresses],
        IsActive = profile?.IsActive ?? true,
        NextMonthGenerationMode = profile?.NextMonthGenerationMode ??
            ExpirationsBrokerProfileDefaults.InitialNextMonthMode(broker.BrokerId),
        Assistants = profile?.Assistants.Select(CopyAssistant).ToList() ?? []
    };

    private static ExpirationsAssistant CopyAssistant(ExpirationsAssistant assistant) => new()
    {
        Id = assistant.Id,
        Name = assistant.Name,
        Email = assistant.Email,
        IsActive = assistant.IsActive
    };
}
