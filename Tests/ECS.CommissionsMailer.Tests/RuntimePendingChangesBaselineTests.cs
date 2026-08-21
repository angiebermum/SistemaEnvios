using ECS.CommissionsMailer.Models;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class RuntimePendingChangesBaselineTests
{
    [Fact]
    public void SavedAtOnlyDoesNotCreatePendingFunctionalChanges()
    {
        var configuration = new AppConfiguration();
        var session = Session();
        var baseline = RuntimePendingChangesBaseline.Capture(configuration, session, [], []);

        session.SavedAt = session.SavedAt.AddHours(3);

        Assert.False(baseline.HasSessionChanges(session));
        Assert.False(baseline.HasChanges(configuration, session, [], []));
    }

    [Fact]
    public void FunctionalSessionEditIsPendingUntilSuccessfullySavedOrDiscarded()
    {
        var configuration = new AppConfiguration();
        var session = Session();
        var baseline = RuntimePendingChangesBaseline.Capture(configuration, session, [], []);

        session.Subject = "Cambio local pendiente";

        Assert.True(baseline.HasSessionChanges(session));
        Assert.True(baseline.HasChanges(configuration, session, [], []));
        var afterSave = baseline.WithSession(session);
        Assert.False(afterSave.HasSessionChanges(session));
    }

    [Fact]
    public void PendingBrokerItemChangeIsDetectedBeforeReloadCanReplaceIt()
    {
        var configuration = new AppConfiguration();
        var session = Session();
        session.BrokerItems.Add(new BrokerSendItem
        {
            BrokerId = Guid.NewGuid(),
            BrokerName = "Corredor",
            IsSelected = false
        });
        var baseline = RuntimePendingChangesBaseline.Capture(configuration, session, [], []);

        session.BrokerItems[0].IsSelected = true;

        Assert.True(baseline.HasSessionChanges(session));
    }

    private static CurrentSession Session() => new()
    {
        Subject = "A",
        Message = "B",
        CommonCcText = "cc@example.test",
        GeneratedPeriod = "2026-08",
        SavedAt = new DateTimeOffset(2026, 8, 10, 12, 0, 0, TimeSpan.Zero)
    };
}
