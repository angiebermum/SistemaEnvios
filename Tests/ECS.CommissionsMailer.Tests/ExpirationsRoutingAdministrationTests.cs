using ECS.CommissionsMailer.Infrastructure.FirebaseClient.Firestore;
using ECS.CommissionsMailer.Models.Expirations;
using ECS.CommissionsMailer.Services.Expirations;
using ECS.CommissionsMailer.Views;

namespace ECS.CommissionsMailer.Tests;

public sealed class ExpirationsRoutingAdministrationTests
{
    private static readonly Guid ShortBroker = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LongBroker = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid AssociationId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ExclusionId = Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee");
    private static readonly DateTimeOffset CreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset UpdatedAt = new(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FullExactNameWinsOverContainedShortNameButConflictingExactAssociationIsAmbiguous()
    {
        var catalog = new[]
        {
            Broker(ShortBroker, "Fernando Cabada"),
            Broker(LongBroker, "Fernando Cabada Corvisier")
        };
        var component = Component("Fernando Cabada Corvisier");

        var exactName = new ExpirationsBrokerResolver(catalog, []).Resolve(component);
        var conflict = new ExpirationsBrokerResolver(
            catalog,
            [Association(ShortBroker, "Fernando Cabada Corvisier")]).Resolve(component);
        var containingBoth = new ExpirationsBrokerResolver(catalog, []).Resolve(
            Component("Agencia Fernando Cabada Corvisier Norte"));

        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved, exactName.Status);
        Assert.Equal(LongBroker, exactName.ResolvedBrokerId);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous, conflict.Status);
        Assert.Equal([ShortBroker, LongBroker], conflict.CandidateBrokerIds.Order());
        Assert.Equal(ExpirationsBrokerResolutionStatus.Ambiguous, containingBoth.Status);
    }

    [Fact]
    public void ExclusionMatchIsExactNonBlockingAndMixedRowKeepsOnlyValidDestinations()
    {
        var catalog = new[] { Broker(ShortBroker, "Ana") };
        var exclusion = Exclusion("Cristian Porras", active: true);
        var read = Read(
            new ExpirationsSourceRow { RowNumber = 2, RawBrokerValue = "Cristian Porras" },
            new ExpirationsSourceRow { RowNumber = 3, RawBrokerValue = "Ana; Cristian Porras" },
            new ExpirationsSourceRow { RowNumber = 4, RawBrokerValue = "Cristian Porras Quesada" });

        var analysis = new ExpirationsWorkbookAnalysisService().Analyze(
            read,
            catalog,
            [],
            [],
            [exclusion]);

        var fullyExcluded = analysis.RowResolutions.Single(row => row.RowNumber == 2);
        var mixed = analysis.RowResolutions.Single(row => row.RowNumber == 3);
        var longerValue = analysis.RowResolutions.Single(row => row.RowNumber == 4);
        Assert.False(fullyExcluded.HasBlockingIssues);
        Assert.Empty(fullyExcluded.DistinctDestinationBrokerIds);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Excluded, Assert.Single(fullyExcluded.Components).Status);
        Assert.False(mixed.HasBlockingIssues);
        Assert.Equal(ShortBroker, Assert.Single(mixed.DistinctDestinationBrokerIds));
        Assert.Contains(mixed.Components, component => component.Status == ExpirationsBrokerResolutionStatus.Excluded);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved, Assert.Single(longerValue.Components).Status);
        Assert.True(longerValue.HasBlockingIssues);
        Assert.Equal(1, analysis.ExcludedRows);
        Assert.Equal(2, analysis.ExcludedComponents);
    }

    [Fact]
    public async Task AssociationsCanBeListedDeactivatedReactivatedAndReassignedWithoutLosingIdentity()
    {
        var original = Association(ShortBroker, "F. Cabada");
        var associations = new FakeAssociationRepository([Stored(original, "a-1")]);
        var exclusions = new FakeExclusionRepository([]);
        var brokers = new FakeConfigurationService(
            BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"),
            BrokerConfiguration(LongBroker, "Fernando Cabada Corvisier", "long@example.test"));
        var service = Service(associations, exclusions, brokers);

        var listed = Assert.Single(await service.ListAssociationsAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ShortBroker, listed.Association.BrokerId);
        Assert.Equal("short@example.test", listed.BrokerPrimaryEmail);

        var deactivated = await service.SetAssociationActiveAsync(
            AssociationId, false, listed.UpdateTime, TestContext.Current.CancellationToken);
        Assert.True(deactivated.WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved,
            Resolver(associations).Resolve(Component("F. Cabada")).Status);

        var afterDeactivate = Assert.Single(await service.ListAssociationsAsync(TestContext.Current.CancellationToken));
        var reactivated = await service.SetAssociationActiveAsync(
            AssociationId, true, afterDeactivate.UpdateTime, TestContext.Current.CancellationToken);
        Assert.True(reactivated.WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Resolved,
            Resolver(associations).Resolve(Component("F. Cabada")).Status);

        var afterReactivate = Assert.Single(await service.ListAssociationsAsync(TestContext.Current.CancellationToken));
        var reassigned = await service.ReassignAsync(
            AssociationId, LongBroker, afterReactivate.UpdateTime, TestContext.Current.CancellationToken);
        Assert.True(reassigned.WasPersisted);
        var stored = Assert.Single(associations.Documents).Value;
        Assert.Equal(AssociationId, stored.Id);
        Assert.Equal(CreatedAt, stored.CreatedAtUtc);
        Assert.Equal(LongBroker, stored.BrokerId);
    }

    [Fact]
    public async Task AssociationExclusionConflictIsBlockedAndDeactivatingExclusionReturnsValueToPending()
    {
        var associations = new FakeAssociationRepository([
            Stored(Association(ShortBroker, "Essential"), "a-1")
        ]);
        var exclusions = new FakeExclusionRepository([
            Stored(Exclusion("Essential", active: false), "e-1")
        ]);
        var brokers = new FakeConfigurationService(
            BrokerConfiguration(ShortBroker, "Fernando Cabada", "short@example.test"));
        var service = Service(associations, exclusions, brokers);

        var blocked = await service.SetExclusionActiveAsync(
            ExclusionId, true, "e-1", TestContext.Current.CancellationToken);
        Assert.Equal(ExpirationsRoutingAdministrationOutcome.Conflict, blocked.Outcome);

        Assert.True((await service.SetAssociationActiveAsync(
            AssociationId, false, "a-1", TestContext.Current.CancellationToken)).WasPersisted);
        Assert.True((await service.SetExclusionActiveAsync(
            ExclusionId, true, "e-1", TestContext.Current.CancellationToken)).WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Excluded,
            Resolver(associations, exclusions).Resolve(Component("Essential")).Status);

        var exclusionVersion = Assert.Single(exclusions.Documents).UpdateTime;
        Assert.True((await service.SetExclusionActiveAsync(
            ExclusionId, false, exclusionVersion, TestContext.Current.CancellationToken)).WasPersisted);
        Assert.Equal(ExpirationsBrokerResolutionStatus.Unresolved,
            Resolver(associations, exclusions).Resolve(Component("Essential")).Status);
    }

    [Fact]
    public void AdministrationSearchFindsAssociationByValueTypeBrokerEmailAndBrokerFilter()
    {
        var state = new ExpirationsRoutingAdministrationState();
        var first = new ExpirationsAssociationAdministrationItem
        {
            Association = Association(ShortBroker, "Fernando Cabada"),
            UpdateTime = "a-1",
            BrokerName = "Fernando Cabada Corvisier",
            BrokerPrimaryEmail = "fernando.corvisier@example.test"
        };
        var secondAssociation = Association(LongBroker, "FC-02");
        secondAssociation.Kind = ExpirationsAssociationKind.Code;
        var second = new ExpirationsAssociationAdministrationItem
        {
            Association = secondAssociation,
            UpdateTime = "a-2",
            BrokerName = "Otra persona",
            BrokerPrimaryEmail = "otra@example.test"
        };
        var configurations = new[]
        {
            BrokerConfiguration(ShortBroker, first.BrokerName, first.BrokerPrimaryEmail),
            BrokerConfiguration(LongBroker, second.BrokerName, second.BrokerPrimaryEmail)
        };
        state.SetData([first, second], [], configurations, null);

        foreach (var query in new[] { "Fernando Cabada", "Alias", "Corvisier", "fernando.corvisier@" })
        {
            state.AssociationSearchText = query;
            Assert.Equal(AssociationId, Assert.Single(state.VisibleAssociations).Association.Id);
        }
        state.AssociationSearchText = string.Empty;
        state.SelectedBrokerFilter = state.BrokerFilters.Single(filter => filter.BrokerId == LongBroker);
        Assert.Equal(LongBroker, Assert.Single(state.VisibleAssociations).Association.BrokerId);
    }

    private static ExpirationsRoutingAdministrationService Service(
        FakeAssociationRepository associations,
        FakeExclusionRepository exclusions,
        FakeConfigurationService brokers) => new(
        associations,
        exclusions,
        brokers,
        timeProvider: new FixedTimeProvider(UpdatedAt.AddHours(1)));

    private static ExpirationsBrokerResolver Resolver(
        FakeAssociationRepository associations,
        FakeExclusionRepository? exclusions = null) => new(
        [Broker(ShortBroker, "Fernando Cabada"), Broker(LongBroker, "Fernando Cabada Corvisier")],
        associations.Documents.Select(document => document.Value),
        exclusions: exclusions?.Documents.Select(document => document.Value));

    private static ExpirationsBrokerCatalogItem Broker(Guid id, string name) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [$"{id:D}@example.test"],
        IsActive = true
    };

    private static ExpirationsBrokerConfigurationItem BrokerConfiguration(Guid id, string name, string email) => new()
    {
        BrokerId = id,
        Name = name,
        PrimaryEmailAddresses = [email],
        IsActive = true
    };

    private static ExpirationsBrokerComponent Component(string value) => new()
    {
        RawValue = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value)
    };

    private static ExpirationsBrokerAssociation Association(Guid brokerId, string value) => new()
    {
        Id = AssociationId,
        BrokerId = brokerId,
        Kind = ExpirationsAssociationKind.Alias,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        IsActive = true,
        CreatedAtUtc = CreatedAt,
        UpdatedAtUtc = UpdatedAt
    };

    private static ExpirationsExclusion Exclusion(string value, bool active) => new()
    {
        Id = ExclusionId,
        Value = value,
        NormalizedValue = new ExpirationsBrokerNormalizer().Normalize(value),
        IsActive = active,
        CreatedAtUtc = CreatedAt,
        UpdatedAtUtc = UpdatedAt
    };

    private static ExpirationsWorkbookReadResult Read(params ExpirationsSourceRow[] rows) => new()
    {
        Status = ExpirationsWorkbookReadStatus.Success,
        Workbook = new ExpirationsSourceWorkbook
        {
            SourcePath = "routing.xlsx",
            WorksheetName = "Detalle",
            HeaderRowNumber = 1,
            BrokerColumnIndex = 1,
            Rows = rows
        }
    };

    private static FirestoreStoredDocument<ExpirationsBrokerAssociation> Stored(
        ExpirationsBrokerAssociation value,
        string version) => new(value, $"modules/vencimientos/associations/{value.Id:D}", version);

    private static FirestoreStoredDocument<ExpirationsExclusion> Stored(
        ExpirationsExclusion value,
        string version) => new(value, $"modules/vencimientos/exclusions/{value.Id:D}", version);

    private sealed class FakeAssociationRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsBrokerAssociation>> documents)
        : IExpirationsBrokerAssociationRepository
    {
        private int _version = 1;
        public List<FirestoreStoredDocument<ExpirationsBrokerAssociation>> Documents { get; } = documents.ToList();
        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>?> GetAsync(
            Guid associationId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.Id == associationId));
        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsBrokerAssociation>>>(Documents.ToList());
        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> CreateAsync(
            ExpirationsBrokerAssociation value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FirestoreStoredDocument<ExpirationsBrokerAssociation>> UpdateAsync(
            ExpirationsBrokerAssociation value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var index = Documents.FindIndex(document => document.Value.Id == value.Id);
            if (index < 0 || Documents[index].UpdateTime != expectedUpdateTime)
                throw new FirestoreConcurrencyException($"associations/{value.Id:D}", expectedUpdateTime);
            var stored = Stored(value, $"a-{++_version}");
            Documents[index] = stored;
            return Task.FromResult(stored);
        }
    }

    private sealed class FakeExclusionRepository(
        IEnumerable<FirestoreStoredDocument<ExpirationsExclusion>> documents)
        : IExpirationsExclusionRepository
    {
        private int _version = 1;
        public List<FirestoreStoredDocument<ExpirationsExclusion>> Documents { get; } = documents.ToList();
        public Task<FirestoreStoredDocument<ExpirationsExclusion>?> GetAsync(
            Guid exclusionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Documents.FirstOrDefault(document => document.Value.Id == exclusionId));
        public Task<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FirestoreStoredDocument<ExpirationsExclusion>>>(Documents.ToList());
        public Task<FirestoreStoredDocument<ExpirationsExclusion>> CreateAsync(
            ExpirationsExclusion value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task<FirestoreStoredDocument<ExpirationsExclusion>> UpdateAsync(
            ExpirationsExclusion value,
            string expectedUpdateTime,
            CancellationToken cancellationToken = default)
        {
            var index = Documents.FindIndex(document => document.Value.Id == value.Id);
            if (index < 0 || Documents[index].UpdateTime != expectedUpdateTime)
                throw new FirestoreConcurrencyException($"exclusions/{value.Id:D}", expectedUpdateTime);
            var stored = Stored(value, $"e-{++_version}");
            Documents[index] = stored;
            return Task.FromResult(stored);
        }
    }

    private sealed class FakeConfigurationService(params ExpirationsBrokerConfigurationItem[] brokers)
        : IExpirationsBrokerConfigurationService
    {
        public Task<IReadOnlyList<ExpirationsBrokerConfigurationItem>> ListAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExpirationsBrokerConfigurationItem>>(brokers);
        public Task<ExpirationsBrokerConfigurationItem?> GetAsync(
            Guid brokerId, CancellationToken cancellationToken = default) =>
            Task.FromResult(brokers.FirstOrDefault(broker => broker.BrokerId == brokerId));
        public Task<ExpirationsBrokerConfigurationSaveResult> SaveAsync(
            ExpirationsBrokerConfigurationItem configuration,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
