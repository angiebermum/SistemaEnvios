using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Configuration;
using ECS.CommissionsMailer.Services;

namespace ECS.CommissionsMailer.Tests;

public sealed class CutoverPreflightStartupPolicyTests
{
    [Fact]
    public void CleanInstallationWithProductionDefaultsEntersPrimaryWithoutAutomaticPreflight()
    {
        var options = new FirebaseClientOptions();
        var state = new FirestoreRuntimeState();

        var execution = CutoverPreflightStartupPolicy.Determine(false, options, state);

        Assert.Equal(RuntimeDataMode.FirestorePrimary, options.RuntimeDataMode);
        Assert.False(options.RequireLegacyCutoverPreflight);
        Assert.Equal(CutoverPreflightExecution.None, execution);
    }

    [Fact]
    public void ManualCutoverCommandContinuesToRequestPreflight()
    {
        var requested = CutoverPreflightStartupPolicy.IsManualCommandRequested(
            ["--firestore-cutover-preflight"]);

        var execution = CutoverPreflightStartupPolicy.Determine(
            requested,
            new FirebaseClientOptions(),
            new FirestoreRuntimeState { HasCloudWrites = true });

        Assert.True(requested);
        Assert.Equal(CutoverPreflightExecution.Manual, execution);
    }

    [Fact]
    public void ExplicitLegacyTransitionWithoutCloudWritesRequiresPreflight()
    {
        var options = new FirebaseClientOptions
        {
            RequireLegacyCutoverPreflight = true
        };

        var execution = CutoverPreflightStartupPolicy.Determine(
            false,
            options,
            new FirestoreRuntimeState());

        Assert.Equal(CutoverPreflightExecution.RequiredLegacyTransition, execution);
    }

    [Fact]
    public void ExplicitLegacyTransitionBlocksWhenPreflightIsNotClean()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            CutoverPreflightStartupPolicy.DemandLegacyPreflightPassed(
                CutoverPreflightExecution.RequiredLegacyTransition,
                canCutOver: false,
                reportPath: @"C:\reports\blocked.json"));

        Assert.Contains("FirestorePrimary fue bloqueado", error.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\reports\blocked.json", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitLegacyTransitionContinuesWhenPreflightIsClean()
    {
        CutoverPreflightStartupPolicy.DemandLegacyPreflightPassed(
            CutoverPreflightExecution.RequiredLegacyTransition,
            canCutOver: true,
            reportPath: @"C:\reports\clean.json");
    }

    [Fact]
    public void ExistingCloudWritesDoNotRepeatAutomaticLegacyPreflight()
    {
        var options = new FirebaseClientOptions
        {
            RequireLegacyCutoverPreflight = true
        };

        var execution = CutoverPreflightStartupPolicy.Determine(
            false,
            options,
            new FirestoreRuntimeState { HasCloudWrites = true });

        Assert.Equal(CutoverPreflightExecution.None, execution);
    }
}
