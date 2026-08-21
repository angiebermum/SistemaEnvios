using ECSCommissionsMailer.FirestoreMigration;
using Google.Cloud.Firestore;

namespace ECS.CommissionsMailer.Tests;

public sealed class MigrationStateUpdateTests
{
    [Fact]
    public void FailedThenCompletedClearsErrorSummary()
    {
        var state = Apply(
            MigrationStateUpdates.Failed("fallo anterior"),
            MigrationStateUpdates.Completed(Counts));

        Assert.True(state.ContainsKey("errorSummary"));
        Assert.Null(state["errorSummary"]);
    }

    [Fact]
    public void FailedThenCompletedClearsFailedAtUtc()
    {
        var state = Apply(
            MigrationStateUpdates.Failed("fallo anterior"),
            MigrationStateUpdates.Completed(Counts));

        Assert.True(state.ContainsKey("failedAtUtc"));
        Assert.Null(state["failedAtUtc"]);
    }

    [Fact]
    public void CompletedAssignsLastMigrationAtUtcAtServerCommitTime()
    {
        var completed = MigrationStateUpdates.Completed(Counts);

        Assert.Same(FieldValue.ServerTimestamp, completed["lastMigrationAtUtc"]);
        Assert.Same(FieldValue.ServerTimestamp, completed["completedAtUtc"]);
    }

    [Fact]
    public void CompletedKeepsDataMigratedTrue()
    {
        Assert.Equal(true, MigrationStateUpdates.Completed(Counts)["dataMigrated"]);
    }

    [Fact]
    public void CompletedKeepsCompletedStatus()
    {
        Assert.Equal("completed", MigrationStateUpdates.Completed(Counts)["status"]);
    }

    [Fact]
    public void RealFailureStillAssignsFailedStateSummaryAndTimestamp()
    {
        var failed = MigrationStateUpdates.Failed("verify falló");

        Assert.Equal("failed", failed["status"]);
        Assert.Equal(false, failed["dataMigrated"]);
        Assert.Equal("verify falló", failed["errorSummary"]);
        Assert.Same(FieldValue.ServerTimestamp, failed["failedAtUtc"]);
    }

    [Fact]
    public void MigrationStateUpdatesTargetOnlyTheTechnicalStateDocument()
    {
        Assert.Equal("system/migrationState", MigrationStateUpdates.DocumentPath);
        Assert.False(MigrationStateUpdates.DocumentPath.StartsWith("settings/", StringComparison.Ordinal));
        Assert.False(MigrationStateUpdates.DocumentPath.StartsWith("brokers/", StringComparison.Ordinal));
        Assert.False(MigrationStateUpdates.DocumentPath.StartsWith("sessions/", StringComparison.Ordinal));
        Assert.False(MigrationStateUpdates.DocumentPath.StartsWith("recentSends/", StringComparison.Ordinal));
        Assert.False(MigrationStateUpdates.DocumentPath.StartsWith("paymentGenerations/", StringComparison.Ordinal));
    }

    private static readonly MigrationCounts Counts = new(1, 67, 1, 67, 0, 18, 1211);

    private static Dictionary<string, object?> Apply(
        params IReadOnlyDictionary<string, object>[] updates)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            foreach (var pair in update)
            {
                state[pair.Key] = pair.Value;
            }
        }

        return state;
    }
}
