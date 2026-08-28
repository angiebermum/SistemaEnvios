using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;

namespace ECS.CommissionsMailer.Services.Expirations;

internal static class ExpirationsBrokerProfileDefaults
{
    public static readonly Guid FelixLaraBrokerId =
        Guid.Parse("6059c919-7198-45b9-a2c3-c6eab96a1503");

    public static ExpirationsNextMonthGenerationMode InitialNextMonthMode(Guid brokerId) =>
        brokerId == FelixLaraBrokerId
            ? ExpirationsNextMonthGenerationMode.SpecialDualSorted
            : ExpirationsNextMonthGenerationMode.Standard;

    public static async Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> EnsureAsync(
        ExpirationsBrokerDirectoryEntry? directory,
        FirestoreStoredDocument<ExpirationsBrokerProfile>? profile,
        IExpirationsBrokerProfileRepository profiles,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (directory?.BrokerId != FelixLaraBrokerId || profile is not null)
            return profile;

        var now = timeProvider.GetUtcNow();
        var initial = new ExpirationsBrokerProfile
        {
            BrokerId = FelixLaraBrokerId,
            IsActive = true,
            NextMonthGenerationMode = ExpirationsNextMonthGenerationMode.SpecialDualSorted,
            Assistants = [],
            CancellationAssistants = [],
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        try
        {
            return await profiles.CreateAsync(initial, cancellationToken);
        }
        catch (FirestoreRestException ex) when (ex.Kind == FirestoreFailureKind.Conflict)
        {
            return await profiles.GetAsync(FelixLaraBrokerId, cancellationToken);
        }
    }
}
