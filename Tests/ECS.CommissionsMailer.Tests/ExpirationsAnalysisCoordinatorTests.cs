using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsAnalysisCoordinatorTests
{
    private static readonly Guid BrokerA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid BrokerB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 8, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SwitchingProcessInEitherDirectionPreservesLoadedCatalogWithoutReload()
    {
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead()),
            new FakeAssociationRepository([]),
            [Broker(BrokerA, "Broker A"), Broker(BrokerB, "Broker B")]);
        coordinator.SelectProcess(ExpirationsProcess.PreviousMonth);
        var loaded = await coordinator.RefreshCatalogAndReanalyzeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, loaded.Catalog.Count);

        coordinator.SelectProcess(ExpirationsProcess.NextMonth);
        Assert.Equal(2, coordinator.Snapshot.Catalog.Count);
        Assert.Null(coordinator.Snapshot.Analysis);
        Assert.Equal(ExpirationsProcess.NextMonth, coordinator.Snapshot.Process);

        coordinator.SelectProcess(ExpirationsProcess.PreviousMonth);
        Assert.Equal(2, coordinator.Snapshot.Catalog.Count);
        Assert.Null(coordinator.Snapshot.Analysis);
        Assert.Equal(ExpirationsProcess.PreviousMonth, coordinator.Snapshot.Process);
    }

    [Fact]
    public async Task ExcludingPendingValuePersistsAndAppliesToBothProcessesWithoutBlocking()
    {
        var exclusions = new FakeExclusionRepository([]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(2, "CRISTIAN PORRAS"))),
            new FakeAssociationRepository([]),
            [Broker(BrokerA, "Broker A")],
            exclusions);
        Prepare(coordinator, "excluded.xlsx");
        var before = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Single(before.PendingIssues);

        var result = await coordinator.ExcludeAsync(2, 0, TestContext.Current.CancellationToken);

        Assert.True(result.Persisted);
        Assert.Empty(result.Snapshot.PendingIssues);
        Assert.Equal("CRISTIAN PORRAS", Assert.Single(result.Snapshot.ExcludedValues).RawValue);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Excluded,
            Assert.Single(result.Snapshot.Analysis!.RowResolutions).Components[0].Status);
        Assert.False(result.Snapshot.Analysis.RowResolutions[0].HasBlockingIssues);

        coordinator.SelectProcess(ExpirationsProcess.NextMonth);
        coordinator.SelectFile("excluded-next.xlsx");
        var next = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Excluded,
            Assert.Single(next.Analysis!.RowResolutions).Components[0].Status);
    }

    [Fact]
    public async Task LoadsCatalogAssociationsAndWorkbookIntoAnalysis()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(1, BrokerA, ExpirationsAssociationKind.Code, "A1"))
        ]);
        var reader = new FakeWorkbookReader(SuccessfulRead(SourceRow(2, "A1")));
        var coordinator = Coordinator(reader, associations, [Broker(BrokerA, "Broker A")]);
        Prepare(coordinator, "first.xlsx");

        var snapshot = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(snapshot.CanGenerate);
        Assert.Equal(1, associations.ListCalls);
        Assert.Equal(1, reader.ReadCalls);
        Assert.Equal(BrokerA, Assert.Single(snapshot.Distribution).BrokerId);
    }

    [Theory]
    [InlineData(ExpirationsAssociationKind.Alias)]
    [InlineData(ExpirationsAssociationKind.Code)]
    public async Task SavesConfirmedUnresolvedAndReanalyzes(ExpirationsAssociationKind kind)
    {
        var associations = new FakeAssociationRepository([]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(10, "VALOR NUEVO"))),
            associations,
            [Broker(BrokerA, "Broker A")]);
        Prepare(coordinator, "new-association.xlsx");
        var before = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(before.CanGenerate);

        var result = await coordinator.ConfirmAssociationAsync(
            new ExpirationsAssociationConfirmation(10, 0, BrokerA, kind),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsAssociationConfirmationOutcome.Created, result.Outcome);
        var created = Assert.Single(associations.Created);
        Assert.Equal(kind, created.Kind);
        Assert.Equal("VALOR NUEVO", created.Value);
        Assert.Equal("VALOR NUEVO", created.NormalizedValue);
        Assert.True(result.Snapshot.CanGenerate);
    }

    [Fact]
    public async Task ExistingActiveAssociationForSameBrokerIsNotDuplicated()
    {
        var associations = new FakeAssociationRepository([]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(11, "ALIAS EXISTENTE"))),
            associations,
            [Broker(BrokerA, "Broker A")]);
        Prepare(coordinator, "existing.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        associations.Documents.Add(Stored(Association(
            1,
            BrokerA,
            ExpirationsAssociationKind.Alias,
            "ALIAS EXISTENTE")));

        var result = await coordinator.ConfirmAssociationAsync(
            new ExpirationsAssociationConfirmation(11, 0, BrokerA, ExpirationsAssociationKind.Alias),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsAssociationConfirmationOutcome.ExistingActive, result.Outcome);
        Assert.Empty(associations.Created);
        Assert.Empty(associations.Updated);
        Assert.True(result.Snapshot.CanGenerate);
    }

    [Fact]
    public async Task ExistingInactiveAssociationIsReactivatedWithUpdateTime()
    {
        var inactive = Stored(Association(
            1,
            BrokerA,
            ExpirationsAssociationKind.Alias,
            "ALIAS INACTIVO",
            active: false), "version-7");
        var associations = new FakeAssociationRepository([inactive]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(12, "ALIAS INACTIVO"))),
            associations,
            [Broker(BrokerA, "Broker A")]);
        Prepare(coordinator, "inactive.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await coordinator.ConfirmAssociationAsync(
            new ExpirationsAssociationConfirmation(12, 0, BrokerA, ExpirationsAssociationKind.Name),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsAssociationConfirmationOutcome.Reactivated, result.Outcome);
        var update = Assert.Single(associations.Updated);
        Assert.Equal("version-7", update.ExpectedUpdateTime);
        Assert.True(update.Value.IsActive);
        Assert.Equal(ExpirationsAssociationKind.Name, update.Value.Kind);
        Assert.Empty(associations.Created);
        Assert.True(result.Snapshot.CanGenerate);
    }

    [Fact]
    public async Task ActiveNormalizedConflictForOtherBrokerUsesSessionOverrideWithoutWrite()
    {
        var associations = new FakeAssociationRepository([]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(13, "CONFLICTO"))),
            associations,
            [Broker(BrokerA, "Broker A"), Broker(BrokerB, "Broker B")]);
        Prepare(coordinator, "conflict.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        associations.Documents.Add(Stored(Association(
            1,
            BrokerB,
            ExpirationsAssociationKind.Alias,
            "CONFLICTO")));

        var result = await coordinator.ConfirmAssociationAsync(
            new ExpirationsAssociationConfirmation(13, 0, BrokerA, ExpirationsAssociationKind.Alias),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsAssociationConfirmationOutcome.SessionOverride, result.Outcome);
        Assert.Empty(associations.Created);
        Assert.Empty(associations.Updated);
        Assert.Equal(BrokerA, Assert.Single(
            Assert.Single(result.Snapshot.Analysis!.RowResolutions).DistinctDestinationBrokerIds));
        Assert.True(result.Snapshot.CanGenerate);
    }

    [Fact]
    public async Task PersistenceFailureLeavesAnalysisBlocked()
    {
        var associations = new FakeAssociationRepository([]) { FailCreate = true };
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(14, "SIN GUARDAR"))),
            associations,
            [Broker(BrokerA, "Broker A")]);
        Prepare(coordinator, "failure.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await coordinator.ConfirmAssociationAsync(
            new ExpirationsAssociationConfirmation(14, 0, BrokerA, ExpirationsAssociationKind.Alias),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsAssociationConfirmationOutcome.Failed, result.Outcome);
        Assert.False(result.Snapshot.CanGenerate);
        Assert.Empty(associations.Documents);
    }

    [Fact]
    public async Task ConcurrencyConflictReloadsAndDoesNotOverwrite()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(1, BrokerA, ExpirationsAssociationKind.Alias, "CAMBIO", false), "old")
        ]) { ThrowConcurrency = true };
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(15, "CAMBIO"))),
            associations,
            [Broker(BrokerA, "Broker A")]);
        Prepare(coordinator, "concurrency.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await coordinator.ConfirmAssociationAsync(
            new ExpirationsAssociationConfirmation(15, 0, BrokerA, ExpirationsAssociationKind.Alias),
            TestContext.Current.CancellationToken);

        Assert.Equal(ExpirationsAssociationConfirmationOutcome.ConcurrencyConflict, result.Outcome);
        Assert.False(result.Snapshot.CanGenerate);
        Assert.True(associations.ListCalls >= 2);
    }

    [Fact]
    public async Task AmbiguousOccurrencesCanChooseDifferentBrokersWithoutFirestoreWrites()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(1, BrokerA, ExpirationsAssociationKind.Code, "AMA")),
            Stored(Association(2, BrokerB, ExpirationsAssociationKind.Code, "AMA"))
        ]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(10, "AMA"), SourceRow(20, "AMA"))),
            associations,
            [Broker(BrokerA, "Broker A"), Broker(BrokerB, "Broker B")]);
        Prepare(coordinator, "ama.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var first = coordinator.ApplyManualOverride(10, 0, BrokerA);
        var second = coordinator.ApplyManualOverride(20, 0, BrokerB);

        Assert.True(first.Applied);
        Assert.True(second.Applied);
        Assert.True(second.Snapshot.CanGenerate);
        Assert.Equal(2, second.Snapshot.ManualOverrides.Count);
        Assert.Equal(BrokerA, Assert.Single(second.Snapshot.Analysis!.RowResolutions[0].DistinctDestinationBrokerIds));
        Assert.Equal(BrokerB, Assert.Single(second.Snapshot.Analysis.RowResolutions[1].DistinctDestinationBrokerIds));
        Assert.Empty(associations.Created);
        Assert.Empty(associations.Updated);
    }

    [Fact]
    public async Task OverrideRejectsUnknownAndInactiveBrokers()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(1, BrokerA, ExpirationsAssociationKind.Code, "AMA")),
            Stored(Association(2, BrokerB, ExpirationsAssociationKind.Code, "AMA"))
        ]);
        var inactive = Guid.Parse("33333333-3333-3333-3333-333333333333");
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(16, "AMA"))),
            associations,
            [Broker(BrokerA, "Broker A"), Broker(BrokerB, "Broker B"), Broker(inactive, "Inactive", false)]);
        Prepare(coordinator, "reject.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(coordinator.ApplyManualOverride(16, 0, Guid.NewGuid()).Applied);
        Assert.False(coordinator.ApplyManualOverride(16, 0, inactive).Applied);
        Assert.False(coordinator.Snapshot.CanGenerate);
    }

    [Fact]
    public async Task ChangingFileAndProcessClearsSessionAnalysisAndOverrides()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(1, BrokerA, ExpirationsAssociationKind.Code, "AMA")),
            Stored(Association(2, BrokerB, ExpirationsAssociationKind.Code, "AMA"))
        ]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(17, "AMA"))),
            associations,
            [Broker(BrokerA, "Broker A"), Broker(BrokerB, "Broker B")]);
        Prepare(coordinator, "old.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(coordinator.ApplyManualOverride(17, 0, BrokerA).Applied);

        coordinator.SelectFile("new.xlsx");
        Assert.Null(coordinator.Snapshot.Analysis);
        Assert.Empty(coordinator.Snapshot.ManualOverrides);
        Assert.EndsWith("new.xlsx", coordinator.Snapshot.SourcePath, StringComparison.OrdinalIgnoreCase);

        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        coordinator.SelectProcess(ExpirationsProcess.NextMonth);
        Assert.Null(coordinator.Snapshot.Analysis);
        Assert.Empty(coordinator.Snapshot.ManualOverrides);
        Assert.EndsWith("new.xlsx", coordinator.Snapshot.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefreshCatalogReanalyzesActiveAndInactiveBrokerWithoutLosingSession()
    {
        var associations = new FakeAssociationRepository([]);
        var directory = new FakeDirectoryRepository([
            new ExpirationsBrokerDirectoryEntry
            {
                BrokerId = BrokerA,
                Name = "Broker A",
                PrimaryEmailAddresses = ["a@example.test"]
            }
        ]);
        var profiles = new FakeProfileRepository([]);
        var coordinator = new ExpirationsAnalysisCoordinator(
            new ExpirationsBrokerCatalogService(directory, profiles),
            associations,
            new FakeWorkbookReader(SuccessfulRead(SourceRow(30, "Broker A"))),
            inspectionService: new FakeInspectionService(),
            sourceHashProvider: _ => "stable-hash");
        Prepare(coordinator, "refresh.xlsx");
        var initial = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(initial.CanGenerate);

        profiles.Documents.Add(StoredProfile(new ExpirationsBrokerProfile
        {
            BrokerId = BrokerA,
            IsActive = false,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        }, "inactive"));
        var inactive = await coordinator.RefreshCatalogAndReanalyzeAsync(TestContext.Current.CancellationToken);
        Assert.False(inactive.CanGenerate);
        Assert.Equal(
            ExpirationsBrokerResolutionStatus.InactiveBroker,
            Assert.Single(Assert.Single(inactive.Analysis!.RowResolutions).Components).Status);

        profiles.Documents[0] = StoredProfile(new ExpirationsBrokerProfile
        {
            BrokerId = BrokerA,
            IsActive = true,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        }, "active");
        var active = await coordinator.RefreshCatalogAndReanalyzeAsync(TestContext.Current.CancellationToken);
        Assert.True(active.CanGenerate);
        Assert.Equal(ExpirationsProcess.PreviousMonth, active.Process);
        Assert.EndsWith("refresh.xlsx", active.SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(associations.Created);
        Assert.Empty(associations.Updated);
    }

    [Fact]
    public async Task RefreshRemovesOverrideThatNowTargetsInactiveBroker()
    {
        var associations = new FakeAssociationRepository([]);
        var directory = new FakeDirectoryRepository([
            new ExpirationsBrokerDirectoryEntry
            {
                BrokerId = BrokerA,
                Name = "Broker A",
                PrimaryEmailAddresses = ["a@example.test"]
            }
        ]);
        var profiles = new FakeProfileRepository([]);
        var coordinator = new ExpirationsAnalysisCoordinator(
            new ExpirationsBrokerCatalogService(directory, profiles),
            associations,
            new FakeWorkbookReader(SuccessfulRead(SourceRow(31, "VALOR SIN ASOCIAR"))),
            inspectionService: new FakeInspectionService(),
            sourceHashProvider: _ => "stable-hash");
        Prepare(coordinator, "override-refresh.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(coordinator.ApplyManualOverride(31, 0, BrokerA).Snapshot.CanGenerate);

        profiles.Documents.Add(StoredProfile(new ExpirationsBrokerProfile
        {
            BrokerId = BrokerA,
            IsActive = false,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        }, "inactive"));
        var refreshed = await coordinator.RefreshCatalogAndReanalyzeAsync(TestContext.Current.CancellationToken);

        Assert.False(refreshed.CanGenerate);
        Assert.Empty(refreshed.ManualOverrides);
        Assert.Equal(
            ExpirationsBrokerResolutionStatus.Unresolved,
            Assert.Single(Assert.Single(refreshed.Analysis!.RowResolutions).Components).Status);
        Assert.Equal(ExpirationsProcess.PreviousMonth, refreshed.Process);
        Assert.EndsWith("override-refresh.xlsx", refreshed.SourcePath, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(associations.Created);
        Assert.Empty(associations.Updated);
    }

    [Fact]
    public async Task PrepareGenerationRefreshesCatalogAndBuildsControlledContext()
    {
        var associations = new FakeAssociationRepository([]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(40, "Broker A"))),
            associations,
            [Broker(BrokerA, "Broker A")]);
        Prepare(coordinator, "prepare.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await coordinator.PrepareGenerationAsync(TestContext.Current.CancellationToken);

        Assert.True(result.CanGenerate);
        Assert.Equal(2, associations.ListCalls);
        Assert.Equal("stable-hash", result.Context!.SourceWorkbookSha256);
        Assert.Equal([40U], result.Context.Analysis.ResolvedRowNumbersByBroker[BrokerA]);
    }

    [Fact]
    public async Task PrepareGenerationBlocksWhenSourceHashChangedAfterAnalysis()
    {
        var currentHash = "hash-before";
        var associations = new FakeAssociationRepository([]);
        var directory = new FakeDirectoryRepository([
            new ExpirationsBrokerDirectoryEntry { BrokerId = BrokerA, Name = "Broker A" }
        ]);
        var coordinator = new ExpirationsAnalysisCoordinator(
            new ExpirationsBrokerCatalogService(directory, new FakeProfileRepository([])),
            associations,
            new FakeWorkbookReader(SuccessfulRead(SourceRow(41, "Broker A"))),
            inspectionService: new FakeInspectionService(),
            sourceHashProvider: _ => currentHash);
        Prepare(coordinator, "hash-change.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        currentHash = "hash-after";

        var result = await coordinator.PrepareGenerationAsync(TestContext.Current.CancellationToken);

        Assert.False(result.CanGenerate);
        Assert.Contains("cambió después del análisis", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareGenerationBlocksWhenRefreshFindsDestinationInactive()
    {
        var associations = new FakeAssociationRepository([]);
        var directory = new FakeDirectoryRepository([
            new ExpirationsBrokerDirectoryEntry { BrokerId = BrokerA, Name = "Broker A" }
        ]);
        var profiles = new FakeProfileRepository([]);
        var coordinator = new ExpirationsAnalysisCoordinator(
            new ExpirationsBrokerCatalogService(directory, profiles),
            associations,
            new FakeWorkbookReader(SuccessfulRead(SourceRow(42, "Broker A"))),
            inspectionService: new FakeInspectionService(),
            sourceHashProvider: _ => "stable");
        Prepare(coordinator, "inactive-before-generate.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        profiles.Documents.Add(StoredProfile(new ExpirationsBrokerProfile
        {
            BrokerId = BrokerA,
            IsActive = false,
            CreatedAtUtc = Now,
            UpdatedAtUtc = Now
        }, "inactive"));

        var result = await coordinator.PrepareGenerationAsync(TestContext.Current.CancellationToken);

        Assert.False(result.CanGenerate);
        Assert.False(result.Snapshot.CanGenerate);
        Assert.Equal(ExpirationsBrokerResolutionStatus.InactiveBroker,
            Assert.Single(Assert.Single(result.Snapshot.Analysis!.RowResolutions).Components).Status);
    }

    [Fact]
    public async Task PrepareGenerationBlocksWhenAssociationBecomesAmbiguousDuringRefresh()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(1, BrokerA, ExpirationsAssociationKind.Code, "CAMBIO"))
        ]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(43, "CAMBIO"))),
            associations,
            [Broker(BrokerA, "Broker A"), Broker(BrokerB, "Broker B")]);
        Prepare(coordinator, "association-change.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        associations.Documents.Add(Stored(Association(2, BrokerB, ExpirationsAssociationKind.Code, "CAMBIO")));

        var result = await coordinator.PrepareGenerationAsync(TestContext.Current.CancellationToken);

        Assert.False(result.CanGenerate);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous,
            Assert.Single(Assert.Single(result.Snapshot.Analysis!.RowResolutions).Components).Status);
    }

    [Fact]
    public async Task PrepareGenerationKeepsValidManualOverrideAfterRefresh()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(1, BrokerA, ExpirationsAssociationKind.Code, "MANUAL")),
            Stored(Association(2, BrokerB, ExpirationsAssociationKind.Code, "MANUAL"))
        ]);
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(44, "MANUAL"))),
            associations,
            [Broker(BrokerA, "Broker A"), Broker(BrokerB, "Broker B")]);
        Prepare(coordinator, "manual-override.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(coordinator.ApplyManualOverride(44, 0, BrokerA).Applied);

        var result = await coordinator.PrepareGenerationAsync(TestContext.Current.CancellationToken);

        Assert.True(result.CanGenerate);
        Assert.Equal(BrokerA, Assert.Single(result.Context!.Analysis.ResolvedRowNumbersByBroker.Keys));
        Assert.Single(result.Snapshot.ManualOverrides);
    }

    [Theory]
    [InlineData(ExpirationsNextMonthGenerationPreflightService.MissingSpecialConfigurationMessage)]
    [InlineData("Existe más de un corredor configurado con el formato especial de mes siguiente.")]
    public async Task NextMonthSpecialConfigurationFailureReturnsBlockedPreparationWithoutEscapingException(
        string message)
    {
        var coordinator = Coordinator(
            new FakeWorkbookReader(SuccessfulRead(SourceRow(45, "Broker A"))),
            new FakeAssociationRepository([]),
            [Broker(BrokerA, "Broker A")],
            nextMonthPreflightService: new RejectingPreflightService(message));
        coordinator.SelectProcess(ExpirationsProcess.NextMonth);
        coordinator.SelectFile("next-month-preflight.xlsx");
        _ = await coordinator.AnalyzeAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await coordinator.PrepareGenerationAsync(
            new ExpirationsPeriod(2026, 9),
            null,
            TestContext.Current.CancellationToken);

        Assert.False(result.CanGenerate);
        Assert.Equal(message, result.ErrorMessage);
    }

    private static ExpirationsAnalysisCoordinator Coordinator(
        IExpirationsWorkbookReader reader,
        FakeAssociationRepository associations,
        IReadOnlyList<ExpirationsBrokerCatalogItem> brokers,
        FakeExclusionRepository? exclusions = null,
        IExpirationsNextMonthGenerationPreflightService? nextMonthPreflightService = null)
    {
        var directory = new FakeDirectoryRepository(brokers.Select(item => new ExpirationsBrokerDirectoryEntry
        {
            BrokerId = item.BrokerId,
            Name = item.Name,
            PrimaryEmailAddresses = item.PrimaryEmailAddresses
        }));
        var profiles = new FakeProfileRepository(brokers
            .Where(item => !item.IsActive)
            .Select(item => new ExpirationsBrokerProfile
            {
                BrokerId = item.BrokerId,
                IsActive = false,
                CreatedAtUtc = Now,
                UpdatedAtUtc = Now
            }));
        return new ExpirationsAnalysisCoordinator(
            new ExpirationsBrokerCatalogService(directory, profiles),
            associations,
            reader,
            inspectionService: new FakeInspectionService(),
            timeProvider: new FixedTimeProvider(Now),
            sourceHashProvider: _ => "stable-hash",
            nextMonthPreflightService: nextMonthPreflightService,
            exclusions: exclusions);
    }

    private static void Prepare(ExpirationsAnalysisCoordinator coordinator, string path)
    {
        coordinator.SelectProcess(ExpirationsProcess.PreviousMonth);
        coordinator.SelectFile(path);
    }

    private static ExpirationsBrokerCatalogItem Broker(Guid id, string name, bool active = true) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{id:D}@example.test"],
        IsActive = active
    };

    private static ExpirationsBrokerAssociation Association(
        int id,
        Guid brokerId,
        ExpirationsAssociationKind kind,
        string value,
        bool active = true) => new()
    {
        Id = Guid.Parse($"{id:x8}-0000-0000-0000-000000000000"),
        BrokerId = brokerId,
        Kind = kind,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        IsActive = active,
        CreatedAtUtc = Now,
        UpdatedAtUtc = Now
    };

    private static FirestoreStoredDocument<ExpirationsBrokerAssociation> Stored(
        ExpirationsBrokerAssociation association,
        string updateTime = "version-1") =>
        new(association, $"modules/vencimientos/associations/{association.Id:D}", updateTime);

    private static FirestoreStoredDocument<ExpirationsBrokerProfile> StoredProfile(
        ExpirationsBrokerProfile profile,
        string updateTime) =>
        new(profile, $"modules/vencimientos/brokerProfiles/{profile.BrokerId:D}", updateTime);

    private static ExpirationsSourceRow SourceRow(uint rowNumber, string value) => new()
    {
        RowNumber = rowNumber,
        RawBrokerValue = value
    };

    private static ExpirationsWorkbookReadResult SuccessfulRead(params ExpirationsSourceRow[] rows) => new()
    {
        Status = ExpirationsWorkbookReadStatus.Success,
        Workbook = new ExpirationsSourceWorkbook
        {
            SourcePath = "fake.xlsx",
            WorksheetName = "Datos",
            HeaderRowNumber = 1,
            BrokerColumnIndex = 1,
            Rows = rows
        }
    };

    private sealed class FakeWorkbookReader(ExpirationsWorkbookReadResult result) : IExpirationsWorkbookReader
    {
        public int ReadCalls { get; private set; }
        public ExpirationsWorkbookReadResult Read(
            string sourcePath,
            ExpirationsWorkbookReadOptions? options = null)
        {
            ReadCalls++;
            return result;
        }
    }

    private sealed class FakeInspectionService : IExpirationsWorkbookInspectionService
    {
        public ExpirationsWorkbookInspection Inspect(string sourcePath) => new() { SourcePath = sourcePath };
    }

    private sealed class FakeDirectoryRepository(IEnumerable<ExpirationsBrokerDirectoryEntry> entries)
        : IExpirationsBrokerDirectoryRepository
    {
        private readonly IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>> _documents =
            entries.Select(value => new FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>(
                value,
                $"brokers/{value.BrokerId:D}",
                "directory-version")).ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_documents.FirstOrDefault(item => item.Value.BrokerId == brokerId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerDirectoryEntry>>> ListAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(_documents);
    }

    private sealed class FakeProfileRepository(IEnumerable<ExpirationsBrokerProfile> profiles)
        : IExpirationsBrokerProfileRepository
    {
        public List<FirestoreStoredDocument<ExpirationsBrokerProfile>> Documents { get; } = profiles
            .Select(value => new FirestoreStoredDocument<ExpirationsBrokerProfile>(
                value,
                $"modules/vencimientos/brokerProfiles/{value.BrokerId:D}",
                "profile-version")).ToList();

        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>?> GetAsync(
            Guid brokerId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(item => item.Value.BrokerId == brokerId));
        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>> ListAsync(
            CancellationToken cancellationToken = default) =>
                Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerProfile>>>(Documents);
        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> CreateAsync(
            ExpirationsBrokerProfile value,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FirestoreStoredDocument<ExpirationsBrokerProfile>> UpdateAsync(
            ExpirationsBrokerProfile value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeAssociationRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsBrokerAssociation>> documents)
        : IExpirationsBrokerAssociationRepository
    {
        private int _version = 10;
        public List<FirestoreStoredDocument<ExpirationsBrokerAssociation>> Documents { get; } = documents.ToList();
        public List<ExpirationsBrokerAssociation> Created { get; } = [];
        public List<UpdateCall> Updated { get; } = [];
        public int ListCalls { get; private set; }
        public bool FailCreate { get; init; }
        public bool ThrowConcurrency { get; init; }

        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>?> GetAsync(
            Guid associationId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(item => item.Value.Id == associationId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>>(Documents.ToList());
        }

        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> CreateAsync(
            ExpirationsBrokerAssociation value,
            CancellationToken cancellationToken = default)
        {
            if (FailCreate) throw new FirestoreRestException(FirestoreFailureKind.Offline, "Sin conexión de prueba.");
            Created.Add(value);
            var stored = Stored(value, $"version-{++_version}");
            Documents.Add(stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> UpdateAsync(
            ExpirationsBrokerAssociation value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            if (ThrowConcurrency)
                throw new FirestoreConcurrencyException(
                    $"modules/vencimientos/associations/{value.Id:D}",
                    expectedUpdateTime);
            Updated.Add(new UpdateCall(value, expectedUpdateTime));
            var index = Documents.FindIndex(item => item.Value.Id == value.Id);
            var stored = Stored(value, $"version-{++_version}");
            Documents[index] = stored;
            return Task.FromResult(stored);
        }

        public Task DeleteAsync(
            Guid associationId,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeExclusionRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsExclusion>> documents)
        : IExpirationsExclusionRepository
    {
        private int _version;
        public List<FirestoreStoredDocument<ExpirationsExclusion>> Documents { get; } = documents.ToList();

        public Task<FirestoreStoredDocument<ExpirationsExclusion>?> GetAsync(
            Guid exclusionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(item => item.Value.Id == exclusionId));

        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>>(Documents.ToList());

        public Task<FirestoreStoredDocument<ExpirationsExclusion>> CreateAsync(
            ExpirationsExclusion value,
            CancellationToken cancellationToken = default)
        {
            var stored = Store(value);
            Documents.Add(stored);
            return Task.FromResult(stored);
        }

        public Task<FirestoreStoredDocument<ExpirationsExclusion>> UpdateAsync(
            ExpirationsExclusion value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var stored = Store(value);
            var index = Documents.FindIndex(item => item.Value.Id == value.Id);
            Documents[index] = stored;
            return Task.FromResult(stored);
        }

        private FirestoreStoredDocument<ExpirationsExclusion> Store(ExpirationsExclusion value) => new(
            value,
            $"modules/vencimientos/exclusions/{value.Id:D}",
            $"exclusion-{++_version}");
    }

    private sealed record UpdateCall(ExpirationsBrokerAssociation Value, string ExpectedUpdateTime);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RejectingPreflightService(string message)
        : IExpirationsNextMonthGenerationPreflightService
    {
        public ExpirationsNextMonthPreflightResult Validate(
            ExpirationsGenerationContext context,
            ExpirationsPeriod? period,
            ExpirationsPremiumColumnOptions? premiumColumnOptions = null,
            CancellationToken cancellationToken = default) =>
            throw new ExpirationsGenerationException(message);
    }
}
